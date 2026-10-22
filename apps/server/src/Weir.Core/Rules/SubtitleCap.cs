using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>Issue #497's <c>subtitle_max_per_language</c>: caps how many subtitle tracks survive per configured language slot, keeping the best by format and type.</summary>
public static partial class RemuxRules
{
    /// <summary>Text formats the "text first" strategy prefers over an image format.</summary>
    private static readonly HashSet<string> TextSubtitleCodecs =
        new(StringComparer.Ordinal) { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text" };

    /// <summary>Image (bitmap) subtitle formats: no text to extract, so a player just overlays the image.</summary>
    private static readonly HashSet<string> ImageSubtitleCodecs =
        new(StringComparer.Ordinal) { "hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "vobsub", "dvb_subtitle", "dvbsub", "xsub" };

    private static readonly string[] OrdinalWords =
        ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    private static string Ordinal(int position) => position < OrdinalWords.Length ? OrdinalWords[position] : $"{position}th";

    /// <summary>A short, human word for a subtitle's format, for the cap's plan notes (e.g. "kept the text track over PGS").</summary>
    private static string SubtitleFormatWord(string codecName)
    {
        var c = RulesJson.Lower(WireStrings.Strip(codecName));
        if (TextSubtitleCodecs.Contains(c))
        {
            return "text";
        }

        return c switch
        {
            "hdmv_pgs_subtitle" or "pgs" => "PGS",
            "dvd_subtitle" or "vobsub" => "VobSub",
            "dvb_subtitle" or "dvbsub" => "DVB subtitle",
            "xsub" => "XSUB",
            "" => "unknown format",
            _ => RulesJson.Upper(c),
        };
    }

    /// <summary>0 (best) to 2 (worst): text over image, or the reverse under <c>image_first</c>. An unrecognized codec sits in the middle.</summary>
    private static long SubtitleFormatRank(string codecName, string strategy)
    {
        var c = RulesJson.Lower(WireStrings.Strip(codecName));
        var isText = TextSubtitleCodecs.Contains(c);
        var isImage = ImageSubtitleCodecs.Contains(c);
        if (strategy == RemuxRuleValues.SubtitleStrategyImageFirst)
        {
            return isImage ? 0 : isText ? 2 : 1;
        }

        return isText ? 0 : isImage ? 2 : 1;
    }

    private enum SubtitleTypeKind
    {
        Regular,
        HearingImpaired,
        Forced,
    }

    /// <summary>A forced track always classifies as forced, even one not exempt from the cap (preserve-forced off).</summary>
    private static SubtitleTypeKind ClassifySubtitleType(SubtitleCandidate candidate) =>
        candidate.ForcedRaw ? SubtitleTypeKind.Forced : candidate.HearingImpaired ? SubtitleTypeKind.HearingImpaired : SubtitleTypeKind.Regular;

    /// <summary>0 (best) to 2 (worst): regular over SDH over forced, or SDH first under <c>accessibility</c>.</summary>
    private static long SubtitleTypeRank(SubtitleTypeKind type, string strategy)
    {
        if (strategy == RemuxRuleValues.SubtitleStrategyAccessibility)
        {
            return type switch
            {
                SubtitleTypeKind.HearingImpaired => 0,
                SubtitleTypeKind.Regular => 1,
                _ => 2,
            };
        }

        return type switch
        {
            SubtitleTypeKind.Regular => 0,
            SubtitleTypeKind.HearingImpaired => 1,
            _ => 2,
        };
    }

    /// <summary>
    /// Issue #497: text (or image, forced) over image, then type (regular over SDH over forced) —
    /// or, under <c>accessibility</c>, SDH first and format only breaks a tie.
    /// </summary>
    private static List<long> SubtitleQualityKey(SubtitleCandidate candidate, string strategy)
    {
        var format = SubtitleFormatRank(candidate.CodecName, strategy);
        var type = SubtitleTypeRank(ClassifySubtitleType(candidate), strategy);
        return strategy == RemuxRuleValues.SubtitleStrategyAccessibility ? [type, format] : [format, type];
    }

    /// <summary>One subtitle stream that matched a configured language, before the per-language cap is applied.</summary>
    private sealed record SubtitleCandidate(int Tier, string Lang, string? Variant, string CodecName, bool HearingImpaired, bool ForcedRaw, PlannedTrack Track)
    {
        public int InputIndex => Track.InputIndex;
    }

    /// <summary>
    /// Issue #497: <c>subtitle_max_per_language</c> (0 = unlimited). Groups the
    /// matched candidates by the configured language slot they matched (<see cref="SubtitleCandidate.Tier"/>,
    /// so a variant-specific slot and its base language are never conflated), keeps every forced
    /// track preserved under <see cref="ProcessingRulesConfig.PreserveForcedSubs"/> unconditionally
    /// (it never counts toward the cap), and otherwise keeps only the best
    /// <see cref="ProcessingRulesConfig.SubtitleMaxPerLanguage"/> by <see cref="SubtitleQualityKey"/>,
    /// noting each drop with the reason (e.g. "kept the text track over PGS").
    /// </summary>
    private static List<PlannedTrack> ApplySubtitleCap(
        List<SubtitleCandidate> candidates, ProcessingRulesConfig config, List<string> notes, List<string> removedSubtitleLabels)
    {
        if (config.SubtitleMaxPerLanguage <= 0)
        {
            return candidates.Select(c => c.Track).ToList();
        }

        var strategy = NormalizeSubtitleQualityStrategy(config.SubtitleQualityStrategy);
        var cap = config.SubtitleMaxPerLanguage;
        var result = new List<PlannedTrack>();

        foreach (var group in candidates.GroupBy(c => c.Tier))
        {
            var groupList = group.ToList();
            var exempt = groupList.Where(c => c.Track.Forced).ToList();
            var pool = groupList.Where(c => !c.Track.Forced).ToList();
            result.AddRange(exempt.Select(c => c.Track));

            if (pool.Count <= cap)
            {
                result.AddRange(pool.Select(c => c.Track));
                continue;
            }

            var ranked = pool
                .Select(c => (Candidate: c, Key: SubtitleQualityKey(c, strategy)))
                .OrderBy(p => p.Key, SortKeyComparer.Instance)
                .ThenBy(p => p.Candidate.InputIndex)
                .Select(p => p.Candidate)
                .ToList();

            var keep = ranked.Take(cap).ToList();
            var drop = ranked.Skip(cap).ToList();
            result.AddRange(keep.Select(c => c.Track));

            var best = keep[0];
            var byFileOrder = groupList.OrderBy(c => c.InputIndex).ToList();
            foreach (var dropped in drop.OrderBy(c => c.InputIndex))
            {
                var position = byFileOrder.FindIndex(c => c.InputIndex == dropped.InputIndex) + 1;
                var langLabel = dropped.Variant is not null ? LanguageVariants.DisplayName(dropped.Variant) : RemuxDisplay.LangDisplayOrBlank(dropped.Lang);
                if (langLabel.Length == 0)
                {
                    langLabel = "Undetermined";
                }

                notes.Add(
                    $"Dropped the {Ordinal(position)} {langLabel} subtitle (stream {dropped.InputIndex}); " +
                    $"kept the {SubtitleFormatWord(best.CodecName)} track over {SubtitleFormatWord(dropped.CodecName)}.");
                removedSubtitleLabels.Add(dropped.Lang.Length > 0 ? dropped.Lang : "und");
            }
        }

        return result;
    }
}
