using System.Globalization;
using System.Text;

namespace Weir.Core.Json;

/// <summary>How <see cref="WireJsonWriter"/> lays out JSON: escaping, separators, indentation and key order.</summary>
/// <param name="EnsureAscii">Escape every non-ASCII character as <c>\uXXXX</c>.</param>
/// <param name="ItemSeparator">Between items.</param>
/// <param name="KeySeparator">Between a key and its value.</param>
/// <param name="Indent">Pretty-print with this many spaces, or <see langword="null"/> for one line.</param>
/// <param name="SortKeys">Sort object keys.</param>
public sealed record WireJsonFormat(bool EnsureAscii, string ItemSeparator, string KeySeparator, int? Indent, bool SortKeys)
{
    /// <summary>API responses: non-ASCII written as-is, no spaces after separators.</summary>
    public static readonly WireJsonFormat Response = new(false, ",", ":", null, false);

    /// <summary>One line, ASCII-escaped, with <c>", "</c> and <c>": "</c> separators.</summary>
    public static readonly WireJsonFormat Default = new(true, ", ", ": ", null, false);

    /// <summary>Two-space indent with sorted keys (configuration snapshots).</summary>
    public static readonly WireJsonFormat IndentedSorted = new(true, ",", ": ", 2, true);

    /// <summary>Two-space indent in insertion order.</summary>
    public static readonly WireJsonFormat Indented = new(true, ",", ": ", 2, false);

    /// <summary>One line, ASCII-escaped, no spaces after separators.</summary>
    public static readonly WireJsonFormat Compact = new(true, ",", ":", null, false);
}

/// <summary>
/// Writes JSON with fixed float formatting, escaping rules and separators so API responses stay
/// byte-identical for existing clients and stored JSON matches what earlier releases wrote.
/// </summary>
public static class WireJsonWriter
{
    public static string Dumps(WireValue value, WireJsonFormat format)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(format);
        var builder = new StringBuilder();
        Write(builder, value, format, 0);
        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> laid out as it would be at nesting <paramref name="level"/> inside a larger document, so a document
    /// that is too long to hold at once can be written one item at a time.
    /// </summary>
    public static string DumpsAtLevel(WireValue value, WireJsonFormat format, int level)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(format);
        var builder = new StringBuilder();
        Write(builder, value, format, level);
        return builder.ToString();
    }

    public static byte[] DumpsUtf8(WireValue value, WireJsonFormat format) => Encoding.UTF8.GetBytes(Dumps(value, format));

    /// <summary>
    /// A float as JSON text: shortest round-trip digits, whole values as <c>1.0</c>, exponent form
    /// (<c>1e+16</c>, <c>1e-05</c>) outside 1e-4 .. 1e16, and <c>NaN</c>/<c>Infinity</c> for non-finite values.
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // Shortest round-trip digits and the decimal exponent, then the fixed-or-exponent layout existing clients expect.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var (digits, decimalPoint) = SplitDigits(shortest);
        var sign = value < 0 ? "-" : string.Empty;
        if (decimalPoint > -4 && decimalPoint <= 16)
        {
            string text;
            if (decimalPoint <= 0)
            {
                text = "0." + new string('0', -decimalPoint) + digits;
            }
            else if (decimalPoint >= digits.Length)
            {
                text = digits + new string('0', decimalPoint - digits.Length) + ".0";
            }
            else
            {
                text = digits[..decimalPoint] + "." + digits[decimalPoint..];
            }

            return sign + text;
        }

        var exponent = decimalPoint - 1;
        var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        var expSign = exponent < 0 ? "-" : "+";
        return sign + mantissa + "e" + expSign + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Digits without leading/trailing zeros and the position of the decimal point (value = 0.digits × 10^point).</summary>
    private static (string Digits, int DecimalPoint) SplitDigits(string invariantText)
    {
        var exponent = 0;
        var mantissa = invariantText;
        var e = invariantText.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(invariantText[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = invariantText[..e];
        }

        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? mantissa : mantissa[..dot];
        var fraction = dot < 0 ? string.Empty : mantissa[(dot + 1)..];
        var all = integerPart + fraction;
        var point = integerPart.Length + exponent;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }

        all = all[leading..];
        point -= leading;
        all = all.TrimEnd('0');
        return (all, point);
    }

    private static void Write(StringBuilder builder, WireValue value, WireJsonFormat format, int level)
    {
        switch (value)
        {
            case WireNull:
                builder.Append("null");
                break;
            case WireBool b:
                builder.Append(b.Value ? "true" : "false");
                break;
            case WireInteger i:
                builder.Append(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case WireNumber f:
                builder.Append(FloatRepr(f.Value));
                break;
            case WireString s:
                WriteString(builder, s.Value, format.EnsureAscii);
                break;
            case WireArray list:
                WriteList(builder, list, format, level);
                break;
            case WireObject dict:
                WriteDict(builder, dict, format, level);
                break;
            default:
                throw new InvalidOperationException("Unknown JSON value.");
        }
    }

    private static void WriteList(StringBuilder builder, WireArray list, WireJsonFormat format, int level)
    {
        if (list.Items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(format.ItemSeparator);
            }

            NewLine(builder, format, level + 1);
            Write(builder, list.Items[i], format, level + 1);
        }

        NewLine(builder, format, level);
        builder.Append(']');
    }

    private static void WriteDict(StringBuilder builder, WireObject dict, WireJsonFormat format, int level)
    {
        if (dict.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        IEnumerable<string> keys = dict.Keys;
        if (format.SortKeys)
        {
            keys = keys.OrderBy(k => k, StringComparer.Ordinal);
        }

        builder.Append('{');
        var first = true;
        foreach (var key in keys)
        {
            if (!first)
            {
                builder.Append(format.ItemSeparator);
            }

            first = false;
            NewLine(builder, format, level + 1);
            WriteString(builder, key, format.EnsureAscii);
            builder.Append(format.KeySeparator);
            Write(builder, dict[key], format, level + 1);
        }

        NewLine(builder, format, level);
        builder.Append('}');
    }

    private static void NewLine(StringBuilder builder, WireJsonFormat format, int level)
    {
        if (format.Indent is { } indent)
        {
            builder.Append('\n').Append(' ', indent * level);
        }
    }

    /// <summary>
    /// Writes a quoted JSON string: short escapes for quote, backslash and common controls, <c>\uXXXX</c> for
    /// other controls and, when <paramref name="ensureAscii"/> is set, for everything above <c>~</c>.
    /// </summary>
    public static void WriteString(StringBuilder builder, string value, bool ensureAscii)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || (ensureAscii && c > '~'))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
