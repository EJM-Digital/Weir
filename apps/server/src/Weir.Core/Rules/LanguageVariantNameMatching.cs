using System.Globalization;
using System.Text;

namespace Weir.Core.Rules;

/// <summary>Detects a regional/script variant from a track's name: marker matching, base-language classification and the table walk.</summary>
public static partial class LanguageVariants
{
    internal static bool IsCjk(char c) => (c >= '⺀' && c <= '鿿') || (c >= '豈' && c <= '﫿');

    private static bool ContainsCjk(string s) => s.Any(IsCjk);

    private static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lower-case, accent-stripped, with <c>.</c>/<c>_</c> folded to a space like a word separator.</summary>
    private static string NormalizeForMatch(string text)
    {
        var lowered = StripDiacritics(text).ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        var lastWasSpace = false;
        foreach (var ch in lowered)
        {
            var c = ch is '_' or '.' ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>A whole-token, case-insensitive substring match: neither side of the match may sit against a letter or digit.</summary>
    private static bool ContainsWholeToken(string haystack, string needle)
    {
        var idx = 0;
        while (true)
        {
            idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            var beforeOk = idx == 0 || !IsWordChar(haystack[idx - 1]);
            var endIdx = idx + needle.Length;
            var afterOk = endIdx >= haystack.Length || !IsWordChar(haystack[endIdx]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            idx++;
        }
    }

    /// <summary>CJK text has no spacing between words, so a CJK marker matches as a plain substring.</summary>
    private static bool MatchesMarker(string normalizedTitle, Marker marker) =>
        ContainsCjk(marker.Normalized) ? normalizedTitle.Contains(marker.Normalized, StringComparison.Ordinal) : ContainsWholeToken(normalizedTitle, marker.Normalized);

    private enum BaseRelation
    {
        /// <summary>No base language is known yet (empty, or <c>und</c>).</summary>
        Undetermined,

        /// <summary>The track's base language is this variant's own family.</summary>
        SameFamily,

        /// <summary>The track's base language is a different, established language.</summary>
        Conflicting,
    }

    private static BaseRelation Classify(string normalizedBase, IReadOnlyList<string> family)
    {
        if (normalizedBase.Length == 0 || normalizedBase == "und")
        {
            return BaseRelation.Undetermined;
        }

        return family.Contains(normalizedBase, StringComparer.Ordinal) ? BaseRelation.SameFamily : BaseRelation.Conflicting;
    }

    /// <summary>
    /// Detects a regional/script variant from an explicit BCP 47 tag first (never overridden), then
    /// from the track's name: self-identifying markers for an undetermined or matching base
    /// language, refine-only markers only for a matching base language, and nothing for a
    /// conflicting one. Regional entries are tried before a broader one for the same language.
    /// </summary>
    public static VariantDetection Detect(string? title, string? baseCode, string? bcp47Tag = null)
    {
        var fromTag = FromBcp47(bcp47Tag);
        if (fromTag.Found)
        {
            return fromTag;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return VariantDetection.None;
        }

        var normalizedBase = RemuxRules.NormalizeLang(baseCode);
        var normalizedTitle = NormalizeForMatch(title);
        if (normalizedTitle.Length == 0)
        {
            return VariantDetection.None;
        }

        foreach (var entry in Table)
        {
            var relation = Classify(normalizedBase, entry.Family);
            if (relation == BaseRelation.Conflicting)
            {
                continue;
            }

            foreach (var marker in entry.SelfIdentifying)
            {
                if (MatchesMarker(normalizedTitle, marker))
                {
                    return new VariantDetection(entry.Identifier, VariantSource.Name, marker.Display);
                }
            }

            if (relation != BaseRelation.SameFamily)
            {
                continue;
            }

            foreach (var marker in entry.RefineOnly)
            {
                if (MatchesMarker(normalizedTitle, marker))
                {
                    return new VariantDetection(entry.Identifier, VariantSource.Name, marker.Display);
                }
            }
        }

        return VariantDetection.None;
    }

    /// <summary>The detected identifier alone, or null: the public entry point issue #496 names.</summary>
    public static string? DetectVariant(string? title, string? baseCode, string? bcp47Tag = null) => Detect(title, baseCode, bcp47Tag).Identifier;
}
