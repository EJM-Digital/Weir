using Weir.Core.Rules;

namespace Weir.Core.Processing;

/// <summary>One kept track's disposition choice from a manual plan (#501): <c>{index, default, forced}</c>.</summary>
public sealed record ManualKeepEntry(int Index, bool Default, bool Forced);

/// <summary>An operator's hand-picked track choice for a held file (#501): <c>{keep, order}</c>.</summary>
public sealed record ManualPlanChoice(IReadOnlyList<ManualKeepEntry> Keep, IReadOnlyList<int> Order);

/// <summary>What kind of track an input index is, for validating and building a manual plan.</summary>
public enum ManualTrackKind
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>
/// Build a <see cref="RemuxPlan"/> directly from an operator's manual track choice (#501), instead of
/// <see cref="RemuxRules.PlanRemux"/>. As Muxarr's custom conversion editor does, the operator's choice is authoritative
/// and skips the automatic mutations <c>PlanRemux</c> applies (candidate ranking, flag-from-name fixes,
/// commentary/hearing-impaired removal, metadata stripping).
/// </summary>
public static class ManualTrackPlan
{
    /// <summary>The one sentence shown for every way a manual plan has gone stale because the source file changed (#501).</summary>
    public const string ChangedMessage = "The file changed since you chose its tracks; choose again";

    /// <summary>Every real (non-image) video, audio and subtitle stream's kind, keyed by its ffprobe index. Embedded
    /// images and attachments are never selectable, so they are left out.</summary>
    public static IReadOnlyDictionary<int, ManualTrackKind> ClassifyIndices(SplitProbeStreams streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var result = new Dictionary<int, ManualTrackKind>();
        var (realVideo, _) = MetadataStreams.SplitVideoAndImages(streams.Video);
        foreach (var stream in realVideo)
        {
            if (stream.Index is { } index)
            {
                result[(int)index] = ManualTrackKind.Video;
            }
        }

        foreach (var stream in streams.Audio)
        {
            if (stream.Index is { } index)
            {
                result[(int)index] = ManualTrackKind.Audio;
            }
        }

        foreach (var stream in streams.Subtitles)
        {
            if (stream.Index is { } index)
            {
                result[(int)index] = ManualTrackKind.Subtitle;
            }
        }

        return result;
    }

    /// <summary>
    /// Validate a choice against a classification of the source's streams. True when the choice is still usable; false
    /// with <paramref name="problem"/> set otherwise (an unknown index, a duplicate index, no video, no audio, more
    /// than one default per type, or an order that does not list exactly the kept indices).
    /// </summary>
    public static bool TryValidate(ManualPlanChoice choice, IReadOnlyDictionary<int, ManualTrackKind> kinds, out string problem)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(kinds);
        problem = string.Empty;
        if (choice.Keep.Count == 0)
        {
            problem = "At least one track must be kept.";
            return false;
        }

        var seen = new HashSet<int>();
        foreach (var entry in choice.Keep)
        {
            if (!seen.Add(entry.Index))
            {
                problem = $"Stream {entry.Index} is listed more than once.";
                return false;
            }

            if (!kinds.ContainsKey(entry.Index))
            {
                problem = $"Stream {entry.Index} does not exist on this file.";
                return false;
            }
        }

        var byKind = choice.Keep.ToLookup(e => kinds[e.Index]);
        if (!byKind[ManualTrackKind.Video].Any())
        {
            problem = "At least one video track must be kept.";
            return false;
        }

        if (!byKind[ManualTrackKind.Audio].Any())
        {
            problem = "At least one audio track must be kept.";
            return false;
        }

        if (byKind[ManualTrackKind.Audio].Count(e => e.Default) > 1)
        {
            problem = "Only one audio track can be set as default.";
            return false;
        }

        if (byKind[ManualTrackKind.Subtitle].Count(e => e.Default) > 1)
        {
            problem = "Only one subtitle track can be set as default.";
            return false;
        }

        var keepIndices = new HashSet<int>(choice.Keep.Select(e => e.Index));
        var order = choice.Order;
        if (order.Count != keepIndices.Count || order.Distinct().Count() != order.Count || !order.All(keepIndices.Contains))
        {
            problem = "The track order must list every kept track exactly once.";
            return false;
        }

        return true;
    }

    /// <summary>Build the plan directly from the choice: no candidate ranking, no name-derived flags, no metadata stripping.</summary>
    public static RemuxPlan BuildPlan(SplitProbeStreams streams, ManualPlanChoice choice)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(choice);
        var kinds = ClassifyIndices(streams);
        var keepIndices = new HashSet<int>(choice.Keep.Select(e => e.Index));
        var rank = choice.Order.Select((index, position) => (index, position)).ToDictionary(p => p.index, p => p.position);
        var ordered = choice.Keep.OrderBy(e => rank.GetValueOrDefault(e.Index, int.MaxValue)).ToList();

        var streamByIndex = streams.Video.Concat(streams.Audio).Concat(streams.Subtitles)
            .Where(s => s.Index is not null)
            .ToDictionary(s => (int)s.Index!.Value);

        var videoIndices = new List<int>();
        var audioTracks = new List<PlannedTrack>();
        var subtitleTracks = new List<PlannedTrack>();
        foreach (var entry in ordered)
        {
            if (!kinds.TryGetValue(entry.Index, out var kind) || !streamByIndex.TryGetValue(entry.Index, out var stream))
            {
                continue;
            }

            switch (kind)
            {
                case ManualTrackKind.Video:
                    videoIndices.Add(entry.Index);
                    break;
                case ManualTrackKind.Audio:
                    audioTracks.Add(TrackFromStream(stream, entry, TrackKind.Audio));
                    break;
                case ManualTrackKind.Subtitle:
                    subtitleTracks.Add(TrackFromStream(stream, entry, TrackKind.Subtitle));
                    break;
            }
        }

        var removedAudio = DescribeRemovedAudio(streams.Audio, keepIndices);
        var removedSubtitles = DescribeRemovedSubtitleLangs(streams.Subtitles, keepIndices);
        // A file always has one default audio track: when the choice marks none (it dropped the track that was), the first kept
        // audio track in the output order becomes it, as on the automatic path.
        var defaultAudioPosition = audioTracks.FindIndex(t => t.Default);
        if (defaultAudioPosition < 0 && audioTracks.Count > 0)
        {
            defaultAudioPosition = 0;
            audioTracks[0] = audioTracks[0] with { Default = true };
        }

        return new RemuxPlan
        {
            VideoIndices = videoIndices,
            Audio = audioTracks,
            Subtitles = subtitleTracks,
            RemovedAudio = removedAudio,
            RemovedSubtitles = removedSubtitles,
            DefaultAudioOutputIndex = defaultAudioPosition >= 0 ? defaultAudioPosition : 0,
            AudioSelectionNotes = ["The operator chose these tracks by hand; automatic selection rules were not applied."],
            RemovedImages = [],
            RemovedAttachments = [],
            MetadataNotes = [],
            Metadata = new MetadataRules(),
        };
    }

    private static PlannedTrack TrackFromStream(ProbeStreamInfo stream, ManualKeepEntry entry, TrackKind kind)
    {
        var lang = RemuxRules.NormalizeLang(stream.Tag("language"));
        var codecName = stream.CodecName;
        var channels = 0;
        if (kind == TrackKind.Audio
            && stream.Get("channels") is { } raw
            && raw.ValueKind is System.Text.Json.JsonValueKind.Number
            && raw.TryGetInt32(out var n))
        {
            channels = n;
        }

        return new PlannedTrack
        {
            InputIndex = entry.Index,
            LangLabel = lang,
            Forced = entry.Forced,
            Default = entry.Default,
            Channels = channels,
            CodecName = codecName,
            Kind = kind,
        };
    }

    private static List<string> DescribeRemovedAudio(IReadOnlyList<ProbeStreamInfo> audio, HashSet<int> keepIndices)
    {
        var removed = new List<string>();
        foreach (var stream in audio)
        {
            if (stream.Index is not { } index || keepIndices.Contains((int)index))
            {
                continue;
            }

            var lang = RemuxRules.NormalizeLang(stream.Tag("language"));
            removed.Add($"{(lang.Length > 0 ? lang : "und")} (stream {index}): removed by manual track choice");
        }

        return removed;
    }

    private static List<string> DescribeRemovedSubtitleLangs(IReadOnlyList<ProbeStreamInfo> subtitles, HashSet<int> keepIndices)
    {
        var removed = new List<string>();
        foreach (var stream in subtitles)
        {
            if (stream.Index is not { } index || keepIndices.Contains((int)index))
            {
                continue;
            }

            var lang = RemuxRules.NormalizeLang(stream.Tag("language"));
            removed.Add(lang.Length > 0 ? lang : "und");
        }

        return removed;
    }
}
