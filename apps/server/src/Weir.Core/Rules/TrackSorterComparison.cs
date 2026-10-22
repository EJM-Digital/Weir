using System.Globalization;
using System.Text.RegularExpressions;
using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>Parses and evaluates a sorter's match expression (<c>"&gt;=5.1"</c>, <c>"!=true"</c>, a plain text match) against a track's fact.</summary>
public static partial class TrackSorters
{
    // Whitespace here includes U+001C..U+001F, which .NET's \s does not, so saved sorters keep parsing the same way.
    private const string WhitespaceClass = "[\\t\\n\\x0b\\x0c\\r\x001c-\x001f \x0085\x00a0\x1680\x2000-\x200a\x2028\x2029\x202f\x205f\x3000]";

    [GeneratedRegex("^" + WhitespaceClass + "*(>=|<=|!=|>|<|=)?" + WhitespaceClass + "*(.+?)" + WhitespaceClass + "*$", RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    /// <summary>
    /// <c>5.1</c> means six channels, and operators write it that way. Null when the text is not a
    /// channel count.
    /// </summary>
    internal static double? ParseChannels(string text)
    {
        var raw = RulesJson.Lower(WireStrings.Strip(text));
        if (raw is "mono" or "1.0")
        {
            return 1.0;
        }

        if (raw is "stereo" or "2.0")
        {
            return 2.0;
        }

        if (raw.Contains('.', StringComparison.Ordinal))
        {
            var dot = raw.IndexOf('.', StringComparison.Ordinal);
            var main = RulesJson.TryIntFromText(raw[..dot]);
            var lfe = RulesJson.TryIntFromText(raw[(dot + 1)..]);
            return main is null || lfe is null ? null : main.Value + lfe.Value;
        }

        return RulesJson.TryIntFromText(raw);
    }

    private static bool Compare(object? actual, string op, string expected, string field)
    {
        if (field == "channels")
        {
            var wanted = ParseChannels(expected);
            var have = (double)Number(actual);
            return wanted is not null && NumericCompare(have, op, wanted.Value);
        }

        if (field == "bitrate")
        {
            var normalized = RulesJson.Lower(WireStrings.Strip(expected)).TrimEnd('k').Replace("_", string.Empty, StringComparison.Ordinal);
            if (RulesJson.TryFloatFromText(normalized) is not { } wantedNumber)
            {
                return false;
            }

            if (RulesJson.Lower(WireStrings.Strip(expected)).EndsWith('k'))
            {
                wantedNumber *= 1000;
            }

            return NumericCompare(Number(actual), op, wantedNumber);
        }

        if (field is "default" or "forced" or "commentary")
        {
            var wantedBool = RulesJson.Lower(WireStrings.Strip(expected)) is "1" or "true" or "yes" or "on";
            var haveBool = Truthy(actual);
            return op == "!=" ? haveBool != wantedBool : haveBool == wantedBool;
        }

        // Text compares case-insensitively; title is a containment test.
        var haveText = RulesJson.Lower(WireStrings.Strip(Truthy(actual) ? Str(actual!) : string.Empty));
        var wantText = RulesJson.Lower(WireStrings.Strip(expected));
        var matched = field == "title" ? haveText.Contains(wantText, StringComparison.Ordinal) : haveText == wantText;
        return op == "!=" ? !matched : matched;
    }

    private static bool NumericCompare(double have, string op, double wanted) => op switch
    {
        ">=" => have >= wanted,
        "<=" => have <= wanted,
        ">" => have > wanted,
        "<" => have < wanted,
        "!=" => have != wanted,
        _ => have == wanted,
    };

    private static (string Operator, string Expected) SplitExpression(string value)
    {
        var match = ComparisonRegex().Match(value);
        if (!match.Success)
        {
            return ("=", WireStrings.Strip(value));
        }

        return (match.Groups[1].Success ? match.Groups[1].Value : "=", match.Groups[2].Value);
    }

    /// <summary>A track fact as a truth value: a flag as it is, a number when not zero, text when not empty; a fact the track does not have is false.</summary>
    private static bool Truthy(object? value) => value switch
    {
        bool b => b,
        long n => n != 0,
        string s => s.Length > 0,
        _ => false,
    };

    /// <summary>The text of a fact that is <see cref="Truthy"/>: a flag that is on reads <c>True</c>.</summary>
    private static string Str(object value) => value switch
    {
        bool => "True",
        long n => n.ToString(CultureInfo.InvariantCulture),
        _ => (string)value,
    };

    /// <summary>A numeric track fact (channels or bitrate), which the track always has.</summary>
    private static long Number(object? actual) => (long)actual!;
}
