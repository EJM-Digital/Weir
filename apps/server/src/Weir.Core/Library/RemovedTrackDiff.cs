using Weir.Core.Rules;

namespace Weir.Core.Library;

/// <summary>One file whose current rules would now keep at least one track that was removed from it (#509).</summary>
public sealed record RemovedTrackDiffResult(RemovedTrackFileKey File, IReadOnlyList<RemovedTrackRecord> TracksNowWanted);

/// <summary>
/// Rules x removed tracks -> titles that would now keep a track that was removed from them (#509).
/// Removal is final (ADR-0017, #505), so this never restores anything; it only flags files worth
/// offering a re-download for.
///
/// <para><b>Audio is approximate.</b> <see cref="RemuxRules.PlanRemux"/> always keeps exactly one audio
/// track (see <c>RemuxPlan.Audio</c>'s single-element list) chosen by ranking every candidate under the
/// configured policy, sorters and languages — this diff has only the removed track's language and codec,
/// not the full candidate set the original probe saw, so it cannot re-run that ranking. It answers a
/// narrower question instead: is this removed track's language one of the configured audio languages
/// (primary/secondary/tertiary)? The exact winner can only be known by re-probing the file, which is what
/// the offered re-download itself does.</para>
///
/// <para><b>Variant-aware</b> (#496, using <see cref="RemovedTrackRecord.Variant"/>): each configured language
/// is compared with <see cref="LanguageVariants.Matches"/>, exactly as
/// <c>RemuxRules.PlanRemux</c> compares a candidate — a plain base-language configuration ("eng") matches any variant of
/// it, while a variant-specific configuration ("fre-CA") matches only a removed track recorded with that same variant,
/// never its base ("fre") or a different variant ("fre-FR").</para>
/// </summary>
public static class RemovedTrackDiff
{
    /// <summary>
    /// The audio languages (or language variants) current rules prefer, normalized and de-duplicated, in the same
    /// order <c>RemuxRules.PlanRemux</c>'s private <c>OrderedPreferenceLangs</c> builds them from
    /// <see cref="ProcessingRulesConfig.PrimaryAudioLang"/>/<see cref="ProcessingRulesConfig.SecondaryAudioLang"/>/
    /// <see cref="ProcessingRulesConfig.TertiaryAudioLang"/> (duplicated here rather than exposed from Rules
    /// because it is a three-line pure helper and Rules' internals stay private to the golden-tested engine).
    /// Normalized with <see cref="LanguageVariants.NormalizeLanguageOrVariant"/>, not <c>RemuxRules.NormalizeLang</c>,
    /// so a configured variant identifier ("fre-CA") survives rather than collapsing to its base ("fre").
    /// </summary>
    private static HashSet<string> PreferredAudioLangs(ProcessingRulesConfig rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var langs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in new[] { rules.PrimaryAudioLang, rules.SecondaryAudioLang, rules.TertiaryAudioLang })
        {
            var lang = LanguageVariants.NormalizeLanguageOrVariant(raw);
            if (lang.Length > 0)
            {
                langs.Add(lang);
            }
        }

        return langs;
    }

    /// <summary>The subtitle languages (or language variants) current rules would keep; empty when subtitle mode removes everything.</summary>
    private static HashSet<string> KeptSubtitleLangs(ProcessingRulesConfig rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.RemovesEverySubtitle)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return new HashSet<string>(rules.SubtitleLangs.Select(LanguageVariants.NormalizeLanguageOrVariant), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="rules"/> would now keep <paramref name="removed"/> if the file still had it.
    /// See the type's remarks for the audio approximation and the variant comparison.
    /// </summary>
    public static bool WouldNowBeKept(ProcessingRulesConfig rules, RemovedTrackRecord removed)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(removed);

        // Keep-all wants every subtitle language back, whatever it was.
        if (removed.Type == RemovedTrackType.Subtitle && rules.KeepsEverySubtitleLanguage)
        {
            return true;
        }

        var configuredLangs = removed.Type switch
        {
            RemovedTrackType.Audio => PreferredAudioLangs(rules),
            RemovedTrackType.Subtitle => KeptSubtitleLangs(rules),
            _ => throw new ArgumentOutOfRangeException(nameof(removed), removed.Type, "Unknown removed track type."),
        };

        return configuredLangs.Any(configured => LanguageVariants.Matches(configured, removed.Language, removed.Variant));
    }

    /// <summary>The removed tracks of one file that current rules would now keep, in their original order.</summary>
    public static IReadOnlyList<RemovedTrackRecord> TracksNowWanted(ProcessingRulesConfig rules, IReadOnlyList<RemovedTrackRecord> removedTracks)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(removedTracks);
        return removedTracks.Where(track => WouldNowBeKept(rules, track)).ToList();
    }

    /// <summary>
    /// Every file (out of everything <paramref name="removedTracksByFile"/> has recorded) that current
    /// rules would now keep at least one removed track for, in the same order the store returned them.
    /// </summary>
    public static IReadOnlyList<RemovedTrackDiffResult> AffectedFiles(
        ProcessingRulesConfig rules,
        IReadOnlyDictionary<RemovedTrackFileKey, IReadOnlyList<RemovedTrackRecord>> removedTracksByFile)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(removedTracksByFile);
        var results = new List<RemovedTrackDiffResult>();
        foreach (var (file, removed) in removedTracksByFile)
        {
            var wanted = TracksNowWanted(rules, removed);
            if (wanted.Count > 0)
            {
                results.Add(new RemovedTrackDiffResult(file, wanted));
            }
        }

        return results;
    }
}
