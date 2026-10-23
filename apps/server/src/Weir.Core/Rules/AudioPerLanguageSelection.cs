using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>Issue #497's <c>audio_keep_mode: per_language</c>: keeps one audio track per configured language slot instead of a single overall winner.</summary>
public static partial class RemuxRules
{
    /// <summary>Builds the kept <see cref="PlannedTrack"/> for one audio candidate, re-reading its own stream's disposition and codec name.</summary>
    private static PlannedTrack BuildPlannedAudioTrack(IReadOnlyList<ProbeStreamInfo> audio, AudioCandidate candidate, bool isDefault)
    {
        var stream = WithIndex(audio).First(p => p.Index == candidate.InputIndex).Stream;
        var disposition = stream.Disposition;
        var codecName = RulesJson.StrOr(stream.Get("codec_name"), string.Empty);

        return new PlannedTrack
        {
            InputIndex = candidate.InputIndex,
            LangLabel = candidate.LangLabel,
            Commentary = candidate.Commentary,
            Forced = disposition.GetValueOrDefault("forced") != 0,
            Default = isDefault,
            Channels = candidate.Channels,
            Lossless = IsLosslessAudio(codecName),
            Bitrate = candidate.Bitrate,
            CodecRank = candidate.CodecRank,
            CodecName = codecName,
            Kind = TrackKind.Audio,
            Variant = candidate.Variant.Identifier,
        };
    }

    /// <summary>
    /// Issue #497: <c>audio_keep_mode: per_language</c>. Keeps the best track (by the configured
    /// sorters) of each configured language slot (primary, then secondary, then tertiary) that has
    /// one, instead of a single overall winner. Never returns an empty list when there is at least
    /// one candidate: a file with no track in any configured slot still keeps its best track
    /// overall, the same "never zero audio" guarantee <see cref="SelectAudioWinner"/> gives single
    /// mode.
    /// </summary>
    private static (List<AudioCandidate> Kept, int DefaultIndex) SelectPerLanguageAudioTracks(
        ProcessingRulesConfig config, List<AudioCandidate> candidates, List<string> notes)
    {
        var sorters = TrackSorters.Parse(config.AudioSortersJson);
        notes.Add($"Track ranking: {TrackSorters.Describe([.. sorters])}.");

        var slots = OrderedPreferenceLangs(config);
        var kept = new List<AudioCandidate>();
        var keptSlotLangs = new List<string>();

        foreach (var slotLang in slots)
        {
            var pool = candidates.Where(c => LanguageVariants.Matches(slotLang, c.LangLabel, c.Variant.Identifier)).ToList();
            if (pool.Count == 0)
            {
                notes.Add($"No {RemuxDisplay.LangDisplay(slotLang)} audio track available for that language slot; skipped.");
                continue;
            }

            var best = PickBest(pool, [], useFallbackPenalty: false, sorters);
            kept.Add(best);
            keptSlotLangs.Add(slotLang);

            var others = pool.Where(c => c.InputIndex != best.InputIndex).OrderBy(c => c.InputIndex).ToList();
            notes.Add(others.Count > 0
                ? $"Kept {DescribeCandidate(best)} for {RemuxDisplay.LangDisplay(slotLang)} over {string.Join("; ", others.Select(DescribeCandidate))}."
                : $"Kept {DescribeCandidate(best)} as the only {RemuxDisplay.LangDisplay(slotLang)} audio track.");
        }

        if (kept.Count == 0 && candidates.Count > 0)
        {
            // Never zero audio: no configured slot matched anything, so fall back to the best
            // candidate overall, exactly as single mode's fallback does.
            var preferredList = OrderedPreferenceLangs(config);
            var fallback = PickBest(candidates, preferredList, useFallbackPenalty: true, sorters);
            kept.Add(fallback);
            keptSlotLangs.Add(fallback.LangLabel);
            notes.Add($"No configured audio language slot matched any track; kept {DescribeCandidate(fallback)} by quality alone (never zero audio).");
        }

        var defaultSlotLang = RulesJson.Lower(WireStrings.Strip(config.DefaultAudioSlot)) switch
        {
            RemuxRuleValues.DefaultAudioSlotSecondary => config.SecondaryAudioLang,
            RemuxRuleValues.DefaultAudioSlotTertiary => config.TertiaryAudioLang,
            _ => config.PrimaryAudioLang,
        };
        var defaultIndex = keptSlotLangs.IndexOf(LanguageVariants.NormalizeLanguageOrVariant(defaultSlotLang));
        if (defaultIndex < 0)
        {
            defaultIndex = kept.Count > 0 ? 0 : -1;
        }

        return (kept, defaultIndex);
    }
}
