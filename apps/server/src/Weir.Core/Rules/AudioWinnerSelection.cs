namespace Weir.Core.Rules;

/// <summary>Picks a single overall winning audio track under the three audio-preference policies (issue #497's <c>audio_keep_mode: single</c>, the default).</summary>
public static partial class RemuxRules
{
    internal static List<string> OrderedPreferenceLangs(ProcessingRulesConfig config)
    {
        var result = new List<string>();
        foreach (var raw in new[] { config.PrimaryAudioLang, config.SecondaryAudioLang, config.TertiaryAudioLang })
        {
            // Issue #496: preserves a recognized variant identifier ("fre-CA") instead of reducing
            // it to its base language; identical to NormalizeLang for every plain code.
            var lang = LanguageVariants.NormalizeLanguageOrVariant(raw);
            if (lang.Length > 0 && !result.Contains(lang))
            {
                result.Add(lang);
            }
        }

        return result;
    }

    /// <summary>The first configured tier a track matches (base or variant), or -1 when none does.</summary>
    private static int TierIndexOf(List<string> preferred, string lang, string? variant)
    {
        for (var i = 0; i < preferred.Count; i++)
        {
            if (LanguageVariants.Matches(preferred[i], lang, variant))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<long> QualitySortKey(AudioCandidate c, int? fallbackPreferredPenalty, IReadOnlyList<TrackSorter> sorters)
    {
        var key = new List<long> { fallbackPreferredPenalty ?? 0 };
        key.AddRange(TrackSorters.SortKeyForTrack(sorters, CandidateAsTrack(c)));
        return key;
    }

    private static AudioCandidate? PickFromHint(List<AudioCandidate> pool, IReadOnlyList<int> hint)
    {
        var byIndex = new Dictionary<int, AudioCandidate>();
        foreach (var candidate in pool)
        {
            byIndex[candidate.InputIndex] = candidate;
        }

        foreach (var index in hint)
        {
            if (byIndex.TryGetValue(index, out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary><c>min(pool, key=...)</c>: the first candidate with the smallest key.</summary>
    private static AudioCandidate PickBest(List<AudioCandidate> pool, List<string> preferredList, bool useFallbackPenalty, IReadOnlyList<TrackSorter> sorters)
    {
        int? PenaltyFor(AudioCandidate c) => useFallbackPenalty
            ? (c.LangLabel.Length > 0 && preferredList.Any(p => LanguageVariants.Matches(p, c.LangLabel, c.Variant.Identifier)) ? 0 : 1)
            : null;

        // Aggregate keeps the earlier candidate on a tie and throws, as min() does, on an empty pool.
        return pool
            .Select(c => (Candidate: c, Key: QualitySortKey(c, PenaltyFor(c), sorters)))
            .Aggregate((best, next) => SortKeyComparer.Instance.Compare(next.Key, best.Key) < 0 ? next : best)
            .Candidate;
    }

    private static AudioCandidate? SelectAudioWinner(ProcessingRulesConfig config, List<AudioCandidate> candidates, List<string> notes)
    {
        var policy = NormalizeAudioPreferenceMode(config.AudioPreferenceMode);
        var preferredList = OrderedPreferenceLangs(config);
        var sorters = TrackSorters.Parse(config.AudioSortersJson);
        notes.Add($"Track ranking: {TrackSorters.Describe([.. sorters])}.");

        // Original-language selection only chooses from the surviving candidates.
        var hint = config.PreferredAudioIndices;
        if (config.OriginalLanguageNote.Length > 0)
        {
            notes.Add(config.OriginalLanguageNote);
        }

        if (hint.Count > 0 && candidates.Count > 0)
        {
            var chosen = PickFromHint(candidates, hint);
            if (chosen is not null)
            {
                notes.Add($"Selected {DescribeCandidate(chosen)} by original language.");
                return chosen;
            }
        }

        if (candidates.Count == 0)
        {
            notes.Add("No eligible audio tracks after commentary and probe rules.");
            return null;
        }

        if (policy == RemuxRuleValues.PolicyQualityAllLanguages)
        {
            var w = PickBest(candidates, preferredList, useFallbackPenalty: false, sorters);
            notes.Add($"Selected {DescribeCandidate(w)} using quality across all languages (policy: quality across all languages).");
            return w;
        }

        if (policy == RemuxRuleValues.PolicyPreferredLangsStrict)
        {
            // Issue #496: a variant identifier is accepted here too, same as the tier walk below.
            var primary = LanguageVariants.NormalizeLanguageOrVariant(config.PrimaryAudioLang);
            if (primary.Length == 0)
            {
                notes.Add("Strict policy requires a primary language; none configured.");
                return null;
            }

            var pool = candidates.Where(c => LanguageVariants.Matches(primary, c.LangLabel, c.Variant.Identifier)).ToList();
            if (pool.Count == 0)
            {
                notes.Add($"No audio tracks matched primary language '{primary}' (strict policy — no fallback to secondary or other languages).");
                return null;
            }

            var w = PickBest(pool, preferredList, useFallbackPenalty: false, sorters);
            notes.Add($"Selected {DescribeCandidate(w)} using primary language only (strict policy).");
            return w;
        }

        // preferred_langs_quality: tier walk, then fallback. Issue #496: a tier that is a plain
        // base language ("fre") matches every variant of it, unchanged; a tier that is a variant
        // identifier ("fre-CA") matches only a track detected as that exact variant.
        foreach (var tierLang in preferredList)
        {
            var pool = candidates.Where(c => LanguageVariants.Matches(tierLang, c.LangLabel, c.Variant.Identifier)).ToList();
            if (pool.Count == 0)
            {
                continue;
            }

            var w = PickBest(pool, preferredList, useFallbackPenalty: false, sorters);
            var others = pool.Where(c => c.InputIndex != w.InputIndex).ToList();
            if (others.Count > 0)
            {
                var otherText = string.Join("; ", others.OrderBy(x => x.InputIndex).Select(DescribeCandidate));
                notes.Add($"Selected {DescribeCandidate(w)} over {otherText} within the same language tier (ranked by quality).");
            }
            else
            {
                notes.Add($"Selected {DescribeCandidate(w)} as the only track in the first matching language tier.");
            }

            return w;
        }

        var fallback = PickBest(candidates, preferredList, useFallbackPenalty: true, sorters);
        var tiers = preferredList.Count > 0 ? string.Join(", ", preferredList) : "none";
        notes.Add(
            $"Fell back to {DescribeCandidate(fallback)} because no track matched configured language tiers ({tiers}); ranked by quality with preferred-language matches first.");
        return fallback;
    }

    /// <summary>
    /// Issue #495 step 5 and issue #496: say when a kept track's dub/audio-description flag came
    /// from its name rather than a stream flag, and which regional/script variant it was detected
    /// as. Shared by the single winner and every track kept under <c>per_language</c>.
    /// </summary>
    private static void AddCandidateFlagNotes(AudioCandidate candidate, List<string> notes)
    {
        if (candidate.Flags.Dub.FromName)
        {
            notes.Add($"The selected track ({DescribeCandidate(candidate)}) looks like a dub track from its name; ffprobe reported no dub flag.");
        }

        if (candidate.Flags.AudioDescription.FromName)
        {
            notes.Add($"The selected track ({DescribeCandidate(candidate)}) looks like an audio-description track from its name; ffprobe reported no such flag.");
        }

        if (candidate.Variant.Found)
        {
            var provenance = candidate.Variant.Source == VariantSource.Tag
                ? $"the language tag '{candidate.Variant.Marker}'"
                : $"the track name '{candidate.Variant.Marker}'";
            notes.Add($"{LanguageVariants.DisplayName(candidate.Variant.Identifier!)}, from {provenance}.");
        }
    }
}
