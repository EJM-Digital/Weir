using System.Globalization;
using System.Numerics;
using System.Text;

namespace Weir.Core.Json;

/// <summary>A value that cannot be converted; callers treat it as bad input. The message wording is fixed because clients see it.</summary>
public sealed class WireValueException : Exception
{
    public WireValueException()
    {
    }

    public WireValueException(string message)
        : base(message)
    {
    }

    public WireValueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A value of the wrong type: not caught by the handlers that catch <see cref="WireValueException"/>,
/// so it surfaces as a 500.
/// </summary>
public sealed class WireTypeException : Exception
{
    public WireTypeException()
    {
    }

    public WireTypeException(string message)
        : base(message)
    {
    }

    public WireTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Text, integer and truthiness conversions of JSON values. Their output (<c>None</c>, <c>True</c>,
/// quoted reprs) appears in stored data and API messages, so it is kept exactly as earlier releases wrote it.
/// </summary>
public static class WireConvert
{
    /// <summary>A string as itself; anything else as <see cref="Repr"/>.</summary>
    public static string Str(WireValue value) => value switch
    {
        WireString s => s.Value,
        _ => Repr(value),
    };

    /// <summary>The display form: <c>None</c>, <c>True</c>/<c>False</c>, quoted strings, <c>[a, b]</c> and <c>{'k': v}</c>.</summary>
    public static string Repr(WireValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            WireNull => "None",
            WireBool b => b.Value ? "True" : "False",
            WireInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
            WireNumber f => FloatRepr(f.Value),
            WireString s => WireStrings.Repr(s.Value),
            WireArray l => "[" + string.Join(", ", l.Items.Select(Repr)) + "]",
            WireObject d => "{" + string.Join(", ", d.Items.Select(p => WireStrings.Repr(p.Key) + ": " + Repr(p.Value))) + "}",
            _ => throw new InvalidOperationException("Unknown JSON value."),
        };
    }

    /// <summary>A float's display form: shortest round-trip digits, and <c>nan</c>, <c>inf</c>, <c>-inf</c>.</summary>
    public static string FloatRepr(double value) =>
        double.IsNaN(value) ? "nan" : double.IsInfinity(value) ? (value > 0 ? "inf" : "-inf") : WireJsonWriter.FloatRepr(value);

    /// <summary>
    /// A JSON value as an integer: booleans are 0/1, floats truncate toward zero, strings parse with
    /// <see cref="TryParseIntLiteral"/>; anything else throws.
    /// </summary>
    public static BigInteger ToInt(WireValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value)
        {
            case WireInteger i:
                return i.Value;
            case WireBool b:
                return b.Value ? BigInteger.One : BigInteger.Zero;
            case WireNumber f:
                if (double.IsNaN(f.Value))
                {
                    throw new WireValueException("NaN is not a whole number.");
                }

                if (double.IsInfinity(f.Value))
                {
                    throw new WireTypeException("Infinity is not a whole number.");
                }

                return new BigInteger(Math.Truncate(f.Value));
            case WireString s:
                if (TryParseIntLiteral(s.Value, out var parsed))
                {
                    return parsed;
                }

                throw new WireValueException($"{WireStrings.Repr(s.Value)} is not a whole number.");
            default:
                throw new WireTypeException($"Expected a whole number but found {KindText(value)}.");
        }
    }

    /// <summary>A base-10 integer literal: surrounding whitespace, a sign, digits with single underscores between them.</summary>
    public static bool TryParseIntLiteral(string raw, out BigInteger value)
    {
        ArgumentNullException.ThrowIfNull(raw);
        value = BigInteger.Zero;
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        if (text.Length == 0 || text[0] == '_' || text[^1] == '_' || text.Contains("__", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '_')
            {
                continue;
            }

            if (!char.IsDigit(c))
            {
                return false;
            }

            digits.Append((char)('0' + (int)char.GetNumericValue(c)));
        }

        value = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            value = -value;
        }

        return true;
    }

    /// <summary>Whether the value counts as set (<see cref="WireValue.IsTruthy"/>).</summary>
    public static bool Bool(WireValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IsTruthy;
    }

    /// <summary>A SQLite storage value from an untyped column as a JSON value; blobs are read as UTF-8 text.</summary>
    public static WireValue FromDatabase(object? value) => value switch
    {
        null or DBNull => WireNull.Instance,
        long l => new WireInteger(l),
        int i => new WireInteger(i),
        double d => new WireNumber(d),
        string s => new WireString(s),
        byte[] bytes => new WireString(Encoding.UTF8.GetString(bytes)),
        bool b => WireValue.Of(b),
        _ => new WireString(string.Create(CultureInfo.InvariantCulture, $"{value}")),
    };

    /// <summary>A JSON value as a SQLite parameter, the way the sqlite3 driver binds it.</summary>
    public static object ToDatabase(WireValue value) => value switch
    {
        WireNull => DBNull.Value,
        WireBool b => b.Value ? 1L : 0L,
        WireInteger i => i.Value >= long.MinValue && i.Value <= long.MaxValue
            ? (long)i.Value
            : throw new WireTypeException("The number is too large to store."),
        WireNumber f => f.Value,
        WireString s => s.Value,
        _ => throw new WireTypeException($"Weir cannot store {KindText(value)} in a single database column."),
    };

    /// <summary>A JSON value's kind in plain words, for error messages.</summary>
    private static string KindText(WireValue value) => value switch
    {
        WireNull => "nothing",
        WireArray => "a list",
        WireObject => "an object",
        _ => "a date and time",
    };
}
