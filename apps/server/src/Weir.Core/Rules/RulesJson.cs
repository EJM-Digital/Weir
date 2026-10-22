using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Weir.Core.Configuration;
using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>
/// The conversions the rules engine applies to ffprobe JSON: truthiness, integer and float
/// parsing, text and repr forms, stripping and string encoding. ffprobe values are loosely typed (a
/// bit rate arrives as a string, a disposition flag may be a string), so these are fixed exactly,
/// including which inputs throw and with what error class and message, to keep plans and error
/// texts identical to the golden files.
/// </summary>
internal static class RulesJson
{
    /// <summary>Invariant lower-casing.</summary>
    public static string Lower(string value) => value.ToLowerInvariant();

    /// <summary>Invariant upper-casing.</summary>
    public static string Upper(string value) => value.ToUpperInvariant();

    // --- JSON values -------------------------------------------------------------------

    /// <summary>The value under <paramref name="name"/> in a parsed JSON object, or null; the last duplicate key wins.</summary>
    public static JsonElement? Get(JsonElement obj, string name)
    {
        JsonElement? found = null;
        foreach (var property in obj.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                found = property.Value;
            }
        }

        return found;
    }

    /// <summary>The value under <paramref name="name"/>; throws a <c>KeyError</c> when absent.</summary>
    public static JsonElement Item(JsonElement obj, string name) =>
        Get(obj, name) ?? throw new RulesInputException("KeyError", $"'{name}'");

    /// <summary>The object's entries, with duplicate keys collapsed to the first position and last value.</summary>
    public static List<KeyValuePair<string, JsonElement>> Items(JsonElement obj)
    {
        var order = new List<string>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!values.ContainsKey(property.Name))
            {
                order.Add(property.Name);
            }

            values[property.Name] = property.Value;
        }

        return order.Select(name => new KeyValuePair<string, JsonElement>(name, values[name])).ToList();
    }

    public static bool IsNone(JsonElement? value) => value is null || value.Value.ValueKind == JsonValueKind.Null;

    public static bool IsDict(JsonElement? value) => value is { ValueKind: JsonValueKind.Object };

    public static bool IsList(JsonElement? value) => value is { ValueKind: JsonValueKind.Array };

    public static bool IsStr(JsonElement? value) => value is { ValueKind: JsonValueKind.String };

    /// <summary>A number with a fraction or exponent is a float; otherwise it is an integer.</summary>
    private static bool IsFloatNumber(JsonElement value) => value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0;

    /// <summary>False for absent, null, false, zero, and an empty string, array or object; true otherwise.</summary>
    public static bool Truthy(JsonElement? value)
    {
        if (value is not { } v)
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => v.GetString()!.Length > 0,
            JsonValueKind.Number => IsFloatNumber(v) ? v.GetDouble() != 0.0 : !BigInteger.Parse(v.GetRawText(), CultureInfo.InvariantCulture).IsZero,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => false, // null and false
        };
    }

    /// <summary>A truthy value as <see cref="Str"/> text, otherwise <paramref name="fallback"/>.</summary>
    public static string StrOr(JsonElement? value, string fallback) => Truthy(value) ? Str(value) : fallback;

    /// <summary>
    /// The text to strip or lower-case: empty for a falsy value; a truthy value that is not a string
    /// throws an <c>AttributeError</c>.
    /// </summary>
    public static string StrMethodTarget(JsonElement? value)
    {
        if (!Truthy(value))
        {
            return string.Empty;
        }

        if (!IsStr(value))
        {
            throw new RulesInputException("AttributeError", $"Expected text but found {KindText(value!.Value)}.");
        }

        return value!.Value.GetString()!;
    }

    /// <summary>
    /// The value as an integer: booleans are 1/0, floats truncate, strings parse as base-10 integers.
    /// Anything else throws a <c>TypeError</c>, <c>ValueError</c> or <c>OverflowError</c>.
    /// </summary>
    public static long Int(JsonElement? value)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new RulesInputException("TypeError", "Expected a whole number but found nothing.");
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.Number when IsFloatNumber(v):
                return FloatToInt(v.GetDouble());
            case JsonValueKind.Number:
                return BigToLong(BigInteger.Parse(v.GetRawText(), CultureInfo.InvariantCulture));
            case JsonValueKind.String:
                return IntFromText(v.GetString()!);
            default:
                throw new RulesInputException("TypeError", $"Expected a whole number but found {KindText(v)}.");
        }
    }

    /// <summary><see cref="Int"/>, returning false for a <c>TypeError</c> or <c>ValueError</c>; an overflow still throws.</summary>
    public static bool TryInt(JsonElement? value, out long result)
    {
        try
        {
            result = Int(value);
            return true;
        }
        catch (RulesInputException error) when (error.ErrorKind is "TypeError" or "ValueError")
        {
            result = 0;
            return false;
        }
    }

    /// <summary>Text as a base-10 integer, surrounding whitespace ignored; throws a <c>ValueError</c> otherwise.</summary>
    public static long IntFromText(string text)
    {
        if (!ValueParsing.TryParseInt(WireStrings.Strip(text), out var parsed))
        {
            throw new RulesInputException("ValueError", $"{Repr(text)} is not a whole number.");
        }

        return parsed;
    }

    /// <summary><see cref="IntFromText"/>, or null where that throws.</summary>
    public static long? TryIntFromText(string text) => ValueParsing.TryParseInt(WireStrings.Strip(text), out var parsed) ? parsed : null;

    private static long FloatToInt(double value)
    {
        if (double.IsInfinity(value))
        {
            throw new RulesInputException("OverflowError", "cannot convert float infinity to integer");
        }

        return BigToLong(new BigInteger(Math.Truncate(value)));
    }

    private static long BigToLong(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? (long)value
            : throw new RulesInputException("OverflowError", "integer is outside the range the .NET engine supports");

    /// <summary>Narrows a parsed integer to the <see cref="int"/> the plan records use.</summary>
    public static int ToInt32(long value) =>
        value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new RulesInputException("OverflowError", "integer is outside the range the .NET engine supports");

    /// <summary>Text as a float (including <c>inf</c>, <c>infinity</c> and <c>nan</c>, any case, optional sign), or null when unreadable.</summary>
    public static double? TryFloatFromText(string text)
    {
        var s = Lower(WireStrings.Strip(text));
        var body = s;
        var sign = 1.0;
        if (body.StartsWith('+') || body.StartsWith('-'))
        {
            sign = body[0] == '-' ? -1.0 : 1.0;
            body = body[1..];
        }

        if (body is "inf" or "infinity")
        {
            return sign * double.PositiveInfinity;
        }

        if (body == "nan")
        {
            return double.NaN;
        }

        if (!IsFloatLiteral(body))
        {
            return null;
        }

        return sign * double.Parse(body.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
    }

    /// <summary>The accepted float literal grammar: digits with single underscores between them, a point, an exponent.</summary>
    private static bool IsFloatLiteral(string body)
    {
        var i = 0;
        var mantissaDigits = ReadDigits(body, ref i);
        if (i < body.Length && body[i] == '.')
        {
            i++;
            mantissaDigits += ReadDigits(body, ref i);
        }

        if (mantissaDigits == 0)
        {
            return false;
        }

        if (i < body.Length && body[i] == 'e')
        {
            i++;
            if (i < body.Length && (body[i] == '+' || body[i] == '-'))
            {
                i++;
            }

            if (ReadDigits(body, ref i) <= 0)
            {
                return false;
            }
        }

        return i == body.Length;
    }

    /// <summary>Digits with single underscores between them. Returns the digit count.</summary>
    private static int ReadDigits(string text, ref int i)
    {
        var count = 0;
        while (i < text.Length)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                count++;
                i++;
            }
            else if (text[i] == '_' && count > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                i++;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    /// <summary>A string as itself; anything else as <see cref="Repr(JsonElement)"/>; absent as <c>None</c>.</summary>
    public static string Str(JsonElement? value)
    {
        if (value is not { } v)
        {
            return "None";
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString()! : Repr(v);
    }

    /// <summary>The repr form the golden files and error messages carry: <c>None</c>, <c>True</c>, quoted strings, <c>[...]</c>, <c>{...}</c>.</summary>
    public static string Repr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.String => Repr(value.GetString()!),
        JsonValueKind.Number when IsFloatNumber(value) => WireConvert.FloatRepr(value.GetDouble()),
        JsonValueKind.Number => BigInteger.Parse(value.GetRawText(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Repr)) + "]",
        JsonValueKind.Object => "{" + string.Join(", ", Items(value).Select(kv => Repr(kv.Key) + ": " + Repr(kv.Value))) + "}",
        _ => "None", // null
    };

    /// <summary>The quoted repr form of a string.</summary>
    public static string Repr(string text) => WireStrings.Repr(text);

    /// <summary>What a value that is neither text nor usable as a whole number is called to a person.</summary>
    private static string KindText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Number when IsFloatNumber(value) => "a decimal number",
        JsonValueKind.Number => "a whole number",
        JsonValueKind.Array => "a list",
        _ => "an object",
    };
}
