namespace Weir.Core.Rules;

/// <summary>Whether a plan actually changes anything about the source file, so an already-conforming file is not remuxed again.</summary>
public static partial class RemuxRules
{
    private static long DispositionFlag(ProbeStreamInfo stream, string name) => stream.Disposition.GetValueOrDefault(name, 0);

    /// <param name="plan">The planned remux.</param>
    /// <param name="audioProbe">The source's audio streams, for the audio comparisons and (#498) their current titles.</param>
    /// <param name="subtitleProbe">The source's subtitle streams, for the subtitle comparisons and (#498) their current titles.</param>
    /// <param name="videoProbe">
    /// #498: the source's video streams, for <see cref="MetadataRules.ClearVideoTrackNames"/>'s current-title
    /// check. Optional, since the golden-file tests do not pass it; when null and the option is on, a video title
    /// is assumed present (the safer of the two guesses) rather than silently skipped.
    /// </param>
    /// <param name="chaptersPresent">
    /// #498: whether the source has any chapters, for <see cref="MetadataRules.RemoveChapters"/>. Defaults to
    /// false for callers that do not probe chapters.
    /// </param>
    public static bool IsRemuxRequired(
        RemuxPlan plan,
        IReadOnlyList<ProbeStreamInfo> audioProbe,
        IReadOnlyList<ProbeStreamInfo> subtitleProbe,
        IReadOnlyList<ProbeStreamInfo>? videoProbe = null,
        bool chaptersPresent = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(audioProbe);
        ArgumentNullException.ThrowIfNull(subtitleProbe);

        // A file whose only change is metadata stripping still needs a pass.
        if (plan.RemovedImages.Count > 0 || plan.RemovedAttachments.Count > 0)
        {
            return true;
        }

        if (plan.Metadata.RemoveTitle || plan.Metadata.RemoveLanguageTags || plan.Metadata.RemoveOtherMetadata)
        {
            return true;
        }

        var probeAudioIndices = WithIndex(audioProbe).Select(p => p.Index).ToList();
        if (!plan.Audio.Select(t => t.InputIndex).SequenceEqual(probeAudioIndices))
        {
            return true;
        }

        var probeSubtitleIndices = WithIndex(subtitleProbe).Select(p => p.Index).ToList();
        if (!plan.Subtitles.Select(t => t.InputIndex).SequenceEqual(probeSubtitleIndices))
        {
            return true;
        }

        var oldAudio = WithIndex(audioProbe).Select(p => (p.Index, DispositionFlag(p.Stream, "default"))).ToList();
        var newAudio = plan.Audio.Select(t => (t.InputIndex, t.Default ? 1L : 0L)).ToList();
        if (!oldAudio.SequenceEqual(newAudio))
        {
            return true;
        }

        var oldSubtitles = WithIndex(subtitleProbe).Select(p => (p.Index, DispositionFlag(p.Stream, "forced"), DispositionFlag(p.Stream, "default"))).ToList();
        var newSubtitles = plan.Subtitles.Select(t => (t.InputIndex, t.Forced ? 1L : 0L, t.Default ? 1L : 0L)).ToList();
        if (!oldSubtitles.SequenceEqual(newSubtitles))
        {
            return true;
        }

        // #498: standardized/cleared track names and chapter removal are changes too, judged against the input's
        // current titles and chapter presence so a file already conforming to the template is not remuxed again.
        if (plan.Metadata.RemoveChapters && chaptersPresent)
        {
            return true;
        }

        if (plan.Metadata.StandardizeTrackNames && TrackTitlesDiffer(plan, audioProbe, subtitleProbe))
        {
            return true;
        }

        return plan.Metadata.ClearVideoTrackNames && VideoTitlesPresent(plan.VideoIndices, videoProbe);
    }

    /// <summary>#498: true when any kept audio or subtitle track's current title does not match what <see cref="TrackNaming"/> would render for it. Only called once the plan's tracks are known to be the probe's own.</summary>
    private static bool TrackTitlesDiffer(RemuxPlan plan, IReadOnlyList<ProbeStreamInfo> audioProbe, IReadOnlyList<ProbeStreamInfo> subtitleProbe)
    {
        var audioByIndex = audioProbe.ToDictionary(IndexOf, s => s);
        foreach (var track in plan.Audio)
        {
            var current = audioByIndex[track.InputIndex].Tag("title") ?? string.Empty;
            if (!string.Equals(current, TrackNaming.RenderTrackName(plan.Metadata, track), StringComparison.Ordinal))
            {
                return true;
            }
        }

        var subtitleByIndex = subtitleProbe.ToDictionary(IndexOf, s => s);
        foreach (var track in plan.Subtitles)
        {
            var current = subtitleByIndex[track.InputIndex].Tag("title") ?? string.Empty;
            if (!string.Equals(current, TrackNaming.RenderTrackName(plan.Metadata, track), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>#498: true when a kept video stream already has a non-empty title, or when there is no video probe to check.</summary>
    private static bool VideoTitlesPresent(IReadOnlyList<int> videoIndices, IReadOnlyList<ProbeStreamInfo>? videoProbe)
    {
        if (videoProbe is null)
        {
            return videoIndices.Count > 0;
        }

        var videoByIndex = videoProbe.ToDictionary(IndexOf, s => s);
        foreach (var index in videoIndices)
        {
            var current = videoByIndex.TryGetValue(index, out var stream) ? stream.Tag("title") ?? string.Empty : string.Empty;
            if (current.Length > 0)
            {
                return true;
            }
        }

        return false;
    }
}
