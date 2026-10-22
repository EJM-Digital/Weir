using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>What to strip. All off by default, so an upgrade changes nothing.</summary>
public sealed record MetadataRules
{
    public bool RemoveImages { get; init; }
    public bool RemoveAttachments { get; init; }
    public bool RemoveTitle { get; init; }
    public bool RemoveLanguageTags { get; init; }
    public bool RemoveOtherMetadata { get; init; }

    /// <summary>#498 (Muxarr-inspired): write a standard <see cref="TrackNameTemplate"/> title on every kept audio and subtitle track.</summary>
    public bool StandardizeTrackNames { get; init; }

    /// <summary>The template rendered by <see cref="TrackNaming"/> when <see cref="StandardizeTrackNames"/> is on and no <see cref="TrackNameOverrides"/> entry matches.</summary>
    public string TrackNameTemplate { get; init; } = TrackNaming.DefaultTemplate;

    /// <summary>Per-flag templates that take priority over <see cref="TrackNameTemplate"/> (forced, then hearing-impaired, then commentary, then audio description).</summary>
    public TrackNameOverrides TrackNameOverrides { get; init; } = new();

    /// <summary>#498: clear scene-tag video track names (e.g. "x265-GROUP") by writing an empty title.</summary>
    public bool ClearVideoTrackNames { get; init; }

    /// <summary>#498: drop the container's chapter list (<c>-map_chapters -1</c>).</summary>
    public bool RemoveChapters { get; init; }

    public bool AnyEnabled =>
        RemoveImages || RemoveAttachments || RemoveTitle || RemoveLanguageTags || RemoveOtherMetadata
        || StandardizeTrackNames || ClearVideoTrackNames || RemoveChapters;
}

/// <summary>
/// Embedded images, attachments and container metadata.
/// An embedded poster is an mjpeg video stream, so the planner separates it from the picture.
/// </summary>
public static class MetadataStreams
{
    private static readonly HashSet<string> ImageCodecs = new(StringComparer.Ordinal) { "mjpeg", "png", "bmp", "gif", "webp", "jpeg", "tiff" };

    /// <summary>The frame rates a still picture reports: none at all, or zero frames a second.</summary>
    private static readonly HashSet<string> StillFrameRates = new(StringComparer.Ordinal) { "", "0/0", "0/1" };

    /// <summary>True for an embedded poster or thumbnail carried as a video stream.</summary>
    public static bool IsImageStream(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var disposition = stream.Get("disposition");
        if (RulesJson.IsDict(disposition))
        {
            var attachedPic = RulesJson.Get(disposition!.Value, "attached_pic");
            if (RulesJson.Truthy(attachedPic) && RulesJson.Int(attachedPic) != 0)
            {
                return true;
            }
        }

        var codec = RulesJson.Lower(WireStrings.Strip(RulesJson.StrOr(stream.Get("codec_name"), string.Empty)));
        if (!ImageCodecs.Contains(codec))
        {
            return false;
        }

        // A codec alone is not proof: a single frame, or a stream with no frame rate, is a still.
        var frames = stream.Get("nb_frames");
        if (!RulesJson.IsNone(frames) && RulesJson.TryInt(frames, out var frameCount) && frameCount <= 1)
        {
            return true;
        }

        var rate = WireStrings.Strip(RulesJson.StrOr(stream.Get("avg_frame_rate"), string.Empty));
        return StillFrameRates.Contains(rate);
    }

    /// <summary>True for an attached font or similar.</summary>
    public static bool IsAttachmentStream(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return RulesJson.Lower(WireStrings.Strip(RulesJson.StrOr(stream.Get("codec_type"), string.Empty))) == "attachment";
    }

    /// <summary>(real video, embedded images). A file of nothing but images keeps its first stream as the picture.</summary>
    public static (IReadOnlyList<ProbeStreamInfo> Real, IReadOnlyList<ProbeStreamInfo> Images) SplitVideoAndImages(IEnumerable<ProbeStreamInfo> videoStreams)
    {
        ArgumentNullException.ThrowIfNull(videoStreams);
        var real = new List<ProbeStreamInfo>();
        var images = new List<ProbeStreamInfo>();
        foreach (var stream in videoStreams)
        {
            (IsImageStream(stream) ? images : real).Add(stream);
        }

        if (real.Count == 0 && images.Count > 0)
        {
            return ([images[0]], images.Skip(1).ToList());
        }

        return (real, images);
    }

    public static string DescribeImageStream(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var codec = WireStrings.Strip(RulesJson.StrOr(stream.Get("codec_name"), "unknown"));
        var width = stream.Get("width");
        var height = stream.Get("height");
        var size = RulesJson.Truthy(width) && RulesJson.Truthy(height) ? $"{RulesJson.Str(width)}x{RulesJson.Str(height)}" : "unknown size";
        return $"embedded image ({codec}, {size})";
    }

    public static string DescribeAttachmentStream(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var tags = stream.Get("tags");
        var name = string.Empty;
        if (RulesJson.IsDict(tags))
        {
            var filename = RulesJson.Get(tags!.Value, "filename");
            var chosen = RulesJson.Truthy(filename) ? filename : RulesJson.Get(tags.Value, "title");
            name = WireStrings.Strip(RulesJson.StrOr(chosen, string.Empty));
        }

        return name.Length > 0 ? $"attachment ({name})" : "attachment";
    }

    /// <summary>The ffmpeg flags for container-level stripping, in the order the golden files record.</summary>
    public static IReadOnlyList<string> ArgvFlags(MetadataRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var flags = new List<string>();
        if (rules.RemoveOtherMetadata)
        {
            flags.AddRange(["-map_metadata", "-1"]);
        }
        else if (rules.RemoveTitle)
        {
            flags.AddRange(["-metadata", "title="]);
        }

        if (rules.RemoveOtherMetadata && rules.RemoveTitle)
        {
            flags.AddRange(["-metadata", "title="]);
        }

        if (rules.RemoveLanguageTags)
        {
            flags.AddRange(["-metadata:s", "language="]);
        }

        // #498: independent of the title/metadata cascade above; appended last so every pre-existing flag keeps
        // its exact position.
        if (rules.RemoveChapters)
        {
            flags.AddRange(["-map_chapters", "-1"]);
        }

        return flags;
    }

    /// <summary>What was stripped, for the before/after display and the activity detail.</summary>
    public static IReadOnlyList<string> RemovalNotes(
        MetadataRules rules,
        IEnumerable<ProbeStreamInfo> removedImages,
        IEnumerable<ProbeStreamInfo> removedAttachments)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(removedImages);
        ArgumentNullException.ThrowIfNull(removedAttachments);
        var notes = new List<string>();
        notes.AddRange(removedImages.Select(s => $"Removed {DescribeImageStream(s)}."));
        notes.AddRange(removedAttachments.Select(s => $"Removed {DescribeAttachmentStream(s)}."));
        if (rules.RemoveTitle)
        {
            notes.Add("Removed the container title.");
        }

        if (rules.RemoveLanguageTags)
        {
            notes.Add("Removed stream language tags.");
        }

        if (rules.RemoveOtherMetadata)
        {
            notes.Add("Removed the remaining container metadata.");
        }

        if (rules.StandardizeTrackNames)
        {
            notes.Add("Standardized audio and subtitle track names.");
        }

        if (rules.ClearVideoTrackNames)
        {
            notes.Add("Cleared the video track name.");
        }

        if (rules.RemoveChapters)
        {
            notes.Add("Removed chapters.");
        }

        return notes;
    }
}
