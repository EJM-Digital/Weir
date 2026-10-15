using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using Weir.Core.Json;

namespace Weir.Core.Tests;

/// <summary>Generators for JSON values as the API reads and writes them, with the awkward text, numbers and nesting a client can send.</summary>
internal static class WireValueGenerators
{
    private static readonly string[] Pieces =
    [
        "a", "Z", "0", " ", "\"", "\\", "/", "\b", "\f", "\n", "\r", "\t", "\u0000", "\u001f", "\u007f", "é", "\u2028", "\u2029", "\uFEFF", "😀", "{", "}", "[", "]", ",", ":",
    ];

    private static readonly string[] LoneSurrogates = ["\ud800", "\udc00", "\udbff"];

    /// <summary>Text that is valid UTF-16 throughout: nothing in it is half of a surrogate pair.</summary>
    public static readonly Gen<string> Text = TextFrom(Pieces);

    /// <summary>Text that may hold half of a surrogate pair, which a string in memory can and the API's bytes cannot.</summary>
    public static readonly Gen<string> TextWithLoneSurrogates = TextFrom([.. Pieces, .. LoneSurrogates]);

    /// <summary>Any 64 bits as a double: every finite value, the infinities, NaN, negative zero and the subnormals.</summary>
    public static readonly Gen<double> AnyDouble =
        from high in Gen.Choose(int.MinValue, int.MaxValue)
        from low in Gen.Choose(int.MinValue, int.MaxValue)
        select BitConverter.Int64BitsToDouble(((long)high << 32) | (uint)low);

    public static readonly Gen<double> FiniteDouble = AnyDouble.Where(double.IsFinite);

    /// <summary>Whole numbers of every size: small, the ends of a long, and far beyond it.</summary>
    public static readonly Gen<BigInteger> AnyInteger = Gen.Frequency(
        (4, Gen.Choose(-5, 5).Select(number => new BigInteger(number))),
        (1, Gen.Elements(new BigInteger(long.MinValue), new BigInteger(long.MaxValue), new BigInteger(long.MaxValue) + 1, new BigInteger(long.MinValue) - 1)),
        (2, from length in Gen.Choose(1, 40)
            from digits in Gen.ArrayOf(Gen.Elements("0123456789".ToCharArray()), length)
            from negative in Gen.Elements(true, false)
            select BigInteger.Parse(new string(digits)) * (negative ? -1 : 1)));

    public static Gen<WireValue> Scalar(bool loneSurrogates) => Gen.OneOf(
        Gen.Constant<WireValue>(WireNull.Instance),
        Gen.Elements(true, false).Select(value => value ? (WireValue)WireBool.True : WireBool.False),
        AnyInteger.Select(value => (WireValue)new WireInteger(value)),
        AnyDouble.Select(value => (WireValue)new WireNumber(value)),
        (loneSurrogates ? TextWithLoneSurrogates : Text).Select(value => (WireValue)new WireString(value)));

    /// <summary>A value of up to <paramref name="depth"/> levels of lists and objects around the scalars.</summary>
    public static Gen<WireValue> Value(int depth, bool loneSurrogates = false) =>
        depth <= 0
            ? Scalar(loneSurrogates)
            : Gen.Frequency<WireValue>(
                (5, Scalar(loneSurrogates)),
                (2, from count in Gen.Choose(0, 4)
                    from items in Gen.ArrayOf(Value(depth - 1, loneSurrogates), count)
                    select (WireValue)new WireArray(items)),
                (2, from count in Gen.Choose(0, 4)
                    from keys in Gen.ArrayOf(loneSurrogates ? TextWithLoneSurrogates : Text, count)
                    from values in Gen.ArrayOf(Value(depth - 1, loneSurrogates), count)
                    select (WireValue)Object(keys, values)));

    /// <summary>Whether two values are the same JSON: a float by its bits (so negative zero is not zero), a NaN as a NaN, an object by its keys in order.</summary>
    public static bool Same(WireValue left, WireValue right, bool keysSorted = false) => (left, right) switch
    {
        (WireNull, WireNull) => true,
        (WireBool a, WireBool b) => a.Value == b.Value,
        (WireInteger a, WireInteger b) => a.Value == b.Value,
        (WireNumber a, WireNumber b) => double.IsNaN(a.Value) ? double.IsNaN(b.Value) : BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(b.Value),
        (WireString a, WireString b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),
        (WireArray a, WireArray b) => a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => Same(pair.First, pair.Second, keysSorted)),
        (WireObject a, WireObject b) => SameObject(a, b, keysSorted),
        _ => false,
    };

    private static bool SameObject(WireObject a, WireObject b, bool keysSorted)
    {
        var left = keysSorted ? a.Keys.Order(StringComparer.Ordinal).ToList() : a.Keys.ToList();
        var right = b.Keys.ToList();
        return left.SequenceEqual(right, StringComparer.Ordinal) && left.All(key => Same(a[key], b[key], keysSorted));
    }

    private static WireObject Object(string[] keys, WireValue[] values)
    {
        var value = new WireObject();
        for (var i = 0; i < keys.Length; i++)
        {
            value.Set(keys[i], values[i]);
        }

        return value;
    }

    private static Gen<string> TextFrom(string[] pieces) =>
        from count in Gen.Choose(0, 8)
        from chosen in Gen.ArrayOf(Gen.Elements(pieces), count)
        select string.Concat(chosen);
}
