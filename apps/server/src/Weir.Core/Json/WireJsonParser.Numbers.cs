using System.Globalization;
using System.Numerics;

namespace Weir.Core.Json;

/// <summary>Scans a JSON number literal, including the <c>NaN</c>/<c>Infinity</c> extensions handled by the scanner.</summary>
public static partial class WireJsonParser
{
    private static WireValue? MatchNumber(string s, int start, out int end)
    {
        end = start;
        var idx = start;
        if (s[idx] == '-')
        {
            idx++;
        }

        if (idx >= s.Length || !char.IsAsciiDigit(s[idx]))
        {
            return null;
        }

        if (s[idx] == '0')
        {
            idx++;
        }
        else
        {
            while (idx < s.Length && char.IsAsciiDigit(s[idx]))
            {
                idx++;
            }
        }

        var isFloat = false;
        if (idx + 1 < s.Length && s[idx] == '.' && char.IsAsciiDigit(s[idx + 1]))
        {
            isFloat = true;
            idx += 2;
            while (idx < s.Length && char.IsAsciiDigit(s[idx]))
            {
                idx++;
            }
        }

        if (idx < s.Length && s[idx] is 'e' or 'E')
        {
            var e = idx + 1;
            if (e < s.Length && s[e] is '+' or '-')
            {
                e++;
            }

            if (e < s.Length && char.IsAsciiDigit(s[e]))
            {
                isFloat = true;
                idx = e;
                while (idx < s.Length && char.IsAsciiDigit(s[idx]))
                {
                    idx++;
                }
            }
        }

        end = idx;
        var text = s[start..idx];
        if (!isFloat && text.Length - (text[0] == '-' ? 1 : 0) > MaxIntegerDigits)
        {
            throw new WireJsonDecodeException($"Integer longer than {MaxIntegerDigits} digits", start);
        }

        return isFloat
            ? new WireNumber(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
            : new WireInteger(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    }
}
