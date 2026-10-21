using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>
/// Remux planning: stream splitting, audio candidate ranking under
/// the three policies, subtitle retention, metadata stripping and whether a pass is needed.
/// </summary>
public static partial class RemuxRules
{
    /// <summary>
    /// A single winning audio track (issue #497: or one per configured language slot under
    /// <c>audio_keep_mode: per_language</c>), the subtitle retention policy and metadata stripping.
    /// Null when no audio would remain.
    /// </summary>
    public static RemuxPlan? PlanRemux(
        IReadOnlyList<ProbeStreamInfo> video,
        IReadOnlyList<ProbeStreamInfo> audio,
        IReadOnlyList<ProbeStreamInfo> subtitles,
        ProcessingRulesConfig config,
        IReadOnlyList<ProbeStreamInfo>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(config);

        var rules = config.Metadata;
        // Always split, so the plan knows which stream is the picture even when the poster is kept.
        var (realVideo, imageStreams) = MetadataStreams.SplitVideoAndImages(video);
        IReadOnlyList<ProbeStreamInfo> droppedImages = rules.RemoveImages ? imageStreams : [];
        var keptVideo = rules.RemoveImages ? realVideo : video;
        var videoIndices = keptVideo.Select(IndexOf).ToList();
        IReadOnlyList<ProbeStreamInfo> droppedAttachments = rules.RemoveAttachments
            ? (attachments ?? []).Where(MetadataStreams.IsAttachmentStream).ToList()
            : [];

        var removedAudio = new List<string>();
        var removedTrackRecords = new List<RemovedTrackRecord>();
        var notes = new List<string>();
        var candidates = new List<AudioCandidate>();

        foreach (var (s, streamIndex) in WithIndex(audio))
        {
            // Issue #495: commentary comes from TrackFlags (disposition first, then the name). For a
            // track with no disposition.comment flag, which covers every golden file, this matches a
            // plain title/comment-tag check.
            var flags = TrackFlagsReader.Detect(s);
            if (config.RemoveCommentary && flags.Commentary.Value)
            {
                var rawLanguageTag = s.Tag("language");
                var lang = NormalizeLang(rawLanguageTag);
                var langLabel = lang.Length > 0 ? lang : "und";
                removedAudio.Add($"{langLabel} (commentary excluded — remove commentary enabled)");
                removedTrackRecords.Add(new RemovedTrackRecord
                {
                    Language = langLabel,
                    Type = RemovedTrackType.Audio,
                    Codec = CodecOrUnknown(s.CodecName),
                    // Issue #496: regional/script variant from the track's name or an explicit BCP 47 tag.
                    Variant = LanguageVariants.DetectVariant(s.Tag("title"), lang, rawLanguageTag),
                    Reason = "commentary excluded — remove commentary enabled",
                });
                notes.Add($"Excluded commentary track (stream {streamIndex}) because remove commentary is enabled.");
                continue;
            }

            candidates.Add(CandidateFromStream(s, streamIndex));
        }

        List<PlannedTrack> keptAudioTracks;
        int defaultAudioOutputIndex;

        if (NormalizeAudioKeepMode(config.AudioKeepMode) == RemuxRuleValues.AudioKeepModePerLanguage)
        {
            var (keptCandidates, defaultIndex) = SelectPerLanguageAudioTracks(config, [.. candidates], notes);
            if (keptCandidates.Count == 0)
            {
                notes.Add("No eligible audio tracks after commentary and probe rules.");
                return null;
            }

            keptAudioTracks = new List<PlannedTrack>(keptCandidates.Count);
            for (var i = 0; i < keptCandidates.Count; i++)
            {
                var candidate = keptCandidates[i];
                AddCandidateFlagNotes(candidate, notes);
                keptAudioTracks.Add(BuildPlannedAudioTrack(audio, candidate, isDefault: i == defaultIndex));
            }

            defaultAudioOutputIndex = defaultIndex;

            var keptIndices = keptAudioTracks.Select(t => t.InputIndex).ToHashSet();
            foreach (var c in candidates.Where(c => !keptIndices.Contains(c.InputIndex)).OrderBy(c => c.InputIndex))
            {
                removedAudio.Add($"{DescribeCandidate(c)}: removed (not selected for its language slot)");
                removedTrackRecords.Add(new RemovedTrackRecord
                {
                    Language = c.LangLabel.Length > 0 ? c.LangLabel : "und",
                    Type = RemovedTrackType.Audio,
                    Codec = CodecOrUnknown(c.CodecName),
                    Variant = c.Variant.Identifier,
                    Reason = "not selected for its language slot",
                });
                notes.Add($"Removed non-selected {DescribeCandidate(c)}.");
            }
        }
        else
        {
            var policy = NormalizeAudioPreferenceMode(config.AudioPreferenceMode);

            var winner = SelectAudioWinner(config, [.. candidates], notes);
            if (winner is null)
            {
                return null;
            }

            AddCandidateFlagNotes(winner, notes);

            // Retention: one winner; every other audio stream is removed.
            var winnerIndex = winner.InputIndex;
            foreach (var c in candidates.Where(c => c.InputIndex != winnerIndex))
            {
                removedAudio.Add($"{DescribeCandidate(c)}: removed (not selected — {DescribeCandidate(winner)} kept)");
                removedTrackRecords.Add(new RemovedTrackRecord
                {
                    Language = c.LangLabel.Length > 0 ? c.LangLabel : "und",
                    Type = RemovedTrackType.Audio,
                    Codec = CodecOrUnknown(c.CodecName),
                    Variant = c.Variant.Identifier,
                    Reason = $"not selected — {DescribeCandidate(winner)} kept",
                });
                notes.Add($"Removed non-selected {DescribeCandidate(c)} after selecting {DescribeCandidate(winner)}.");
            }

            if (policy == RemuxRuleValues.PolicyPreferredLangsQuality)
            {
                var preferred = OrderedPreferenceLangs(config);
                // Issue #496: a variant tier ("fre-CA") only ever matches its own variant, so this
                // looks up the tier by base-or-variant match instead of the track's plain language.
                var winnerTier = TierIndexOf(preferred, winner.LangLabel, winner.Variant.Identifier);
                if (preferred.Count > 1 && winnerTier >= 0)
                {
                    foreach (var c in candidates)
                    {
                        if (c.InputIndex == winnerIndex)
                        {
                            continue;
                        }

                        if (TierIndexOf(preferred, c.LangLabel, c.Variant.Identifier) > winnerTier)
                        {
                            notes.Add(
                                $"Ignored {DescribeCandidate(c)} because preferred languages (tiered quality) had candidates in '{winner.LangLabel}' first.");
                        }
                    }
                }
            }

            keptAudioTracks = [BuildPlannedAudioTrack(audio, winner, isDefault: true)];
            defaultAudioOutputIndex = 0;
        }

        var keptSubtitles = new List<PlannedTrack>();
        var removedSubtitleLabels = new List<string>();
        if (config.RemovesEverySubtitle)
        {
            foreach (var s in subtitles)
            {
                var rawLanguageTag = s.Tag("language");
                var lang = NormalizeLang(rawLanguageTag);
                var langLabel = lang.Length > 0 ? lang : "und";
                removedSubtitleLabels.Add(langLabel);
                removedTrackRecords.Add(new RemovedTrackRecord
                {
                    Language = langLabel,
                    Type = RemovedTrackType.Subtitle,
                    Codec = CodecOrUnknown(s.CodecName),
                    // Issue #496: regional/script variant from the track's name or an explicit BCP 47 tag.
                    Variant = LanguageVariants.DetectVariant(s.Tag("title"), lang, rawLanguageTag),
                    Reason = "subtitle mode removes every subtitle track",
                });
            }
        }
        else
        {
            // Issue #537 item 3: configured subtitle languages are normalized the same way a
            // track's own tag is, so "ENG" or "en-US" matches a file tagged "eng". Issue #496: a
            // repeated language keeps its last position (as before); a variant identifier
            // ("fre-CA") is kept distinct from its base ("fre") rather than collapsed into it.
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var n = 0; n < config.SubtitleLangs.Count; n++)
            {
                rank[LanguageVariants.NormalizeLanguageOrVariant(config.SubtitleLangs[n])] = n;
            }

            // Keep-all: every language is wanted. Each language (or variant, or untagged) is its own slot, in the
            // order it first appears in the file, so the per-language cap still means per language.
            var keepAll = config.KeepsEverySubtitleLanguage;
            var seenSlots = new Dictionary<string, int>(StringComparer.Ordinal);

            var subtitleCandidates = new List<SubtitleCandidate>();

            foreach (var (s, index) in WithIndex(subtitles))
            {
                var rawLanguageTag = s.Tag("language");
                var lang = NormalizeLang(rawLanguageTag);
                var langLabel = lang.Length > 0 ? lang : "und";
                var disposition = s.Disposition;

                // A variant-specific tier ("fre-CA") takes priority over a broader base tier
                // ("fre") when a track matches both; a plain base tier still matches every variant.
                var variant = LanguageVariants.DetectVariant(s.Tag("title"), lang, rawLanguageTag);
                int matchedTier;
                if (keepAll)
                {
                    var slot = variant ?? langLabel;
                    if (!seenSlots.TryGetValue(slot, out matchedTier))
                    {
                        matchedTier = seenSlots.Count;
                        seenSlots[slot] = matchedTier;
                    }
                }
                else
                {
                    matchedTier = variant is not null && rank.TryGetValue(variant, out var variantRank)
                        ? variantRank
                        : rank.GetValueOrDefault(lang, -1);
                }

                if (!keepAll && (lang.Length == 0 || matchedTier < 0))
                {
                    removedSubtitleLabels.Add(langLabel);
                    removedTrackRecords.Add(new RemovedTrackRecord
                    {
                        Language = langLabel,
                        Type = RemovedTrackType.Subtitle,
                        Codec = CodecOrUnknown(s.CodecName),
                        Variant = variant,
                        Reason = "language not kept by subtitle rules",
                    });
                    continue;
                }

                // Issue #495: forced also comes from the name (a signs track counts as forced),
                // and a hearing-impaired track can be dropped outright when the rule is enabled.
                var flags = TrackFlagsReader.Detect(s);
                if (config.RemoveHearingImpairedSubs && flags.HearingImpaired.Value)
                {
                    removedSubtitleLabels.Add(langLabel);
                    removedTrackRecords.Add(new RemovedTrackRecord
                    {
                        Language = langLabel,
                        Type = RemovedTrackType.Subtitle,
                        Codec = CodecOrUnknown(s.CodecName),
                        Variant = variant,
                        Reason = "hearing-impaired subtitle removed",
                    });
                    var hiSource = flags.HearingImpaired.Source == TrackFlagSource.Disposition ? "its hearing-impaired flag" : "its name";
                    notes.Add($"Removed hearing-impaired subtitle track (stream {index}) because remove hearing-impaired subtitles is enabled ({hiSource}).");
                    continue;
                }

                var forced = config.PreserveForcedSubs && flags.Forced.Value;
                if (forced && flags.Forced.Source == TrackFlagSource.Name)
                {
                    notes.Add($"Subtitle track (stream {index}) counts as forced because its name says so; ffprobe reported no forced flag.");
                }

                var track = new PlannedTrack
                {
                    InputIndex = index,
                    LangLabel = lang,
                    Forced = forced,
                    Default = config.PreserveDefaultSubs && disposition.GetValueOrDefault("default") != 0,
                    Kind = TrackKind.Subtitle,
                    Variant = variant,
                };

                subtitleCandidates.Add(new SubtitleCandidate(
                    Tier: matchedTier,
                    Lang: lang,
                    Variant: variant,
                    CodecName: RulesJson.StrOr(s.Get("codec_name"), string.Empty),
                    HearingImpaired: flags.HearingImpaired.Value,
                    ForcedRaw: flags.Forced.Value,
                    Track: track));
            }

            // Issue #497: cap how many subtitles survive per configured language slot (0 =
            // unlimited). A forced track kept under preserve-forced is exempt.
            keptSubtitles = ApplySubtitleCap(subtitleCandidates, config, notes, removedSubtitleLabels);

            // Keep-all leaves the file's own order; keep-selected orders by the configured language list.
            keptSubtitles = keepAll
                ? [.. keptSubtitles.OrderBy(t => t.InputIndex)]
                : [.. keptSubtitles
                    .OrderBy(t => t.Variant is not null && rank.TryGetValue(t.Variant, out var vr) ? vr : rank.GetValueOrDefault(t.LangLabel, 99))
                    .ThenBy(t => t.InputIndex)];

            // Subtitle order: the profile's saved ranking orders the kept subtitles, so the ranking a person saves is
            // the one the output has. The order above breaks ties, because the key ends in the position a track already has.
            var subtitleSorters = SubtitleOrder(config);
            if (subtitleSorters.Count > 0)
            {
                var position = keptSubtitles.Select((t, i) => (t.InputIndex, i)).ToDictionary(p => p.InputIndex, p => p.i);
                keptSubtitles = [.. keptSubtitles.OrderBy(
                    t => SubtitleSortKey(subtitleSorters, t, keepAll ? null : rank, position[t.InputIndex]),
                    SortKeyComparer.Instance)];
                notes.Add($"Subtitle order: {TrackSorters.Describe(subtitleSorters)}.");
            }
        }

        return new RemuxPlan
        {
            VideoIndices = videoIndices,
            Audio = keptAudioTracks,
            Subtitles = keptSubtitles,
            RemovedAudio = removedAudio,
            RemovedSubtitles = removedSubtitleLabels,
            RemovedTrackRecords = removedTrackRecords,
            DefaultAudioOutputIndex = defaultAudioOutputIndex,
            AudioSelectionNotes = notes,
            RemovedImages = droppedImages.Select(MetadataStreams.DescribeImageStream).ToList(),
            RemovedAttachments = droppedAttachments.Select(MetadataStreams.DescribeAttachmentStream).ToList(),
            MetadataNotes = MetadataStreams.RemovalNotes(rules, droppedImages, droppedAttachments),
            Metadata = rules,
        };
    }

    /// <summary>The saved subtitle order, or none: unlike audio, no saved order means "keep the order above", not a seeded default.</summary>
    private static IReadOnlyList<TrackSorter> SubtitleOrder(ProcessingRulesConfig config) =>
        WireStrings.Strip(config.SubtitleSortersJson).Length == 0 ? [] : TrackSorters.Parse(config.SubtitleSortersJson);

    /// <summary>
    /// One kept subtitle's place under the saved order. A "language" criterion with no value ranks by the languages-to-keep
    /// list (the order a person put them in), not by the alphabet; under keep-all every language ties there.
    /// </summary>
    private static List<long> SubtitleSortKey(
        IReadOnlyList<TrackSorter> sorters, PlannedTrack track, Dictionary<string, int>? languageRank, int position)
    {
        var sortable = new SortableTrack
        {
            Index = track.InputIndex,
            Language = track.LangLabel,
            Forced = track.Forced,
            Default = track.Default,
            Codec = track.CodecName,
            CodecRank = track.CodecRank,
        };
        var parts = new List<long>();
        foreach (var sorter in sorters)
        {
            if (sorter.Field == "language" && sorter.Value is null)
            {
                var place = languageRank is null
                    ? 0
                    : track.Variant is not null && languageRank.TryGetValue(track.Variant, out var variantPlace)
                        ? variantPlace
                        : languageRank.GetValueOrDefault(track.LangLabel, 99);
                parts.Add(sorter.Reversed ? -place : place);
                continue;
            }

            parts.AddRange(TrackSorters.SorterKeyComponent(sorter, sortable));
        }

        parts.Add(position);
        return parts;
    }

    /// <summary>Sane defaults for remux planning.</summary>
    public static ProcessingRulesConfig DefaultConfig() => new()
    {
        PrimaryAudioLang = "eng",
        SecondaryAudioLang = "jpn",
        TertiaryAudioLang = string.Empty,
        DefaultAudioSlot = RemuxRuleValues.DefaultAudioSlotPrimary,
        RemoveCommentary = true,
        SubtitleMode = RemuxRuleValues.SubtitleModeRemoveAll,
        SubtitleLangs = [],
        PreserveForcedSubs = true,
        PreserveDefaultSubs = true,
        AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality,
        RemoveHearingImpairedSubs = false,
        AudioKeepMode = RemuxRuleValues.AudioKeepModeSingle,
        SubtitleMaxPerLanguage = 0,
        SubtitleQualityStrategy = RemuxRuleValues.SubtitleStrategyTextFirst,
    };
}
