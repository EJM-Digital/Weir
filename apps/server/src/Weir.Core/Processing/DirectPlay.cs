namespace Weir.Core.Processing;

/// <summary>One device's playback capabilities.</summary>
public sealed record DeviceProfile(
    string Id,
    string Name,
    string Source,
    string Note,
    IReadOnlySet<string> ContainersYes,
    IReadOnlySet<string> ContainersMaybe,
    IReadOnlyDictionary<string, DeviceVideoLimits> Video,
    IReadOnlyDictionary<string, DeviceVideoLimits> VideoMaybe,
    IReadOnlySet<string> AudioYes,
    IReadOnlySet<string> AudioMaybe);

/// <summary>A codec's playable limits (<c>max_height</c>, <c>max_bit_depth</c>, allowed <c>containers</c>).</summary>
public sealed record DeviceVideoLimits(int? MaxHeight, int? MaxBitDepth, IReadOnlySet<string>? Containers);

/// <summary>What the pass measured. Null means not measured, and is never read as "no".</summary>
public sealed record MediaFacts(string? Container, string? VideoCodec, long? VideoHeight, long? VideoBitDepth, IReadOnlyList<string>? AudioCodecs);

/// <summary>One device's answer for one file.</summary>
public sealed record DirectPlayVerdict(string DeviceId, string DeviceName, string Verdict, IReadOnlyList<string> Reasons);

/// <summary>
/// Which of the operator's devices can play a file without conversion (#467).
/// Information only: nothing here may feed a processing decision.
/// </summary>
public static class DirectPlayEvaluation
{
    public const string OverrideFileName = "direct-play-devices.json";

    private static readonly Dictionary<string, string> AudioLabels = new(StringComparer.Ordinal)
    {
        ["aac"] = "AAC",
        ["ac3"] = "Dolby Digital",
        ["eac3"] = "Dolby Digital Plus",
        ["dts"] = "DTS",
        ["truehd"] = "Dolby TrueHD",
        ["flac"] = "FLAC",
        ["opus"] = "Opus",
        ["vorbis"] = "Vorbis",
        ["mp3"] = "MP3",
        ["alac"] = "Apple Lossless",
        ["pcm"] = "PCM",
    };

    private static readonly Dictionary<string, string> VideoLabels = new(StringComparer.Ordinal)
    {
        ["h264"] = "H.264",
        ["hevc"] = "HEVC",
        ["vp9"] = "VP9",
        ["av1"] = "AV1",
        ["mpeg4"] = "MPEG-4",
        ["mpeg2video"] = "MPEG-2",
    };

    /// <summary>The container a file's extension names, lower-cased; null when it has none.</summary>
    public static string? ContainerForPath(string relativePath)
    {
        var suffix = Path.GetExtension(relativePath);
        return suffix.Length > 1 ? suffix[1..].ToLowerInvariant() : null;
    }

    private static string AudioFamily(string codec)
    {
        var value = codec.Trim().ToLowerInvariant();
        return value.StartsWith("pcm_", StringComparison.Ordinal) ? "pcm" : value;
    }

    private static (bool Fits, string? Why) VideoFits(DeviceVideoLimits limits, MediaFacts facts)
    {
        if (limits.MaxHeight is { } maxHeight && facts.VideoHeight is { } height && height > maxHeight)
        {
            return (false, $"its {height}p video is above {maxHeight}p");
        }

        if (limits.MaxBitDepth is { } maxDepth && facts.VideoBitDepth is { } depth && depth > maxDepth)
        {
            return (false, $"its {depth}-bit video is above {maxDepth}-bit");
        }

        if (limits.Containers is { Count: > 0 } containers && facts.Container is { } container && !containers.Contains(container))
        {
            return (false, $"it plays that video only in {string.Join(", ", containers.Select(c => c.ToUpperInvariant()))}");
        }

        return (true, null);
    }

    /// <summary>One device's answer, with reasons an operator can act on.</summary>
    public static DirectPlayVerdict Evaluate(DeviceProfile profile, MediaFacts facts)
    {
        var no = new List<string>();
        var maybe = new List<string>();
        var unknown = false;

        if (facts.Container is null)
        {
            unknown = true;
        }
        else if (!profile.ContainersYes.Contains(facts.Container))
        {
            (profile.ContainersMaybe.Contains(facts.Container) ? maybe : no).Add($"{facts.Container.ToUpperInvariant()} files");
        }

        var codec = (facts.VideoCodec ?? string.Empty).Trim().ToLowerInvariant();
        var label = VideoLabels.GetValueOrDefault(codec, codec.ToUpperInvariant());
        if (codec.Length == 0)
        {
            unknown = true;
        }
        else if (profile.Video.TryGetValue(codec, out var limits))
        {
            var (fits, why) = VideoFits(limits, facts);
            if (!fits)
            {
                if (profile.VideoMaybe.TryGetValue(codec, out var maybeLimits) && VideoFits(maybeLimits, facts).Fits)
                {
                    maybe.Add($"{label} video ({why})");
                }
                else
                {
                    no.Add($"{label} video ({why})");
                }
            }
        }
        else if (profile.VideoMaybe.TryGetValue(codec, out var maybeOnlyLimits))
        {
            var (fits, why) = VideoFits(maybeOnlyLimits, facts);
            (fits ? maybe : no).Add($"{label} video" + (why is not null ? $" ({why})" : string.Empty));
        }
        else
        {
            no.Add($"{label} video");
        }

        if (facts.AudioCodecs is null)
        {
            unknown = true;
        }
        else if (facts.AudioCodecs.Count > 0)
        {
            var families = facts.AudioCodecs.Select(AudioFamily).ToList();
            var playable = families.Where(f => profile.AudioYes.Contains(f)).ToList();
            var partial = families.Where(f => profile.AudioMaybe.Contains(f)).ToList();
            var unplayable = families.Where(f => !profile.AudioYes.Contains(f) && !profile.AudioMaybe.Contains(f)).Distinct().Order(StringComparer.Ordinal).ToList();
            var names = string.Join(", ", unplayable.Select(f => AudioLabels.GetValueOrDefault(f, f.ToUpperInvariant())));
            if (playable.Count == 0 && partial.Count == 0)
            {
                no.Add($"{names} audio");
            }
            else if (unplayable.Count > 0)
            {
                maybe.Add($"{names} audio on some tracks");
            }
            else if (partial.Count > 0 && playable.Count == 0)
            {
                maybe.Add(string.Join(", ", partial.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(f => AudioLabels.GetValueOrDefault(f, f.ToUpperInvariant()))) + " audio");
            }
        }

        if (no.Count > 0)
        {
            return new DirectPlayVerdict(profile.Id, profile.Name, "no", [.. no.Select(r => $"cannot play {r}")]);
        }

        if (maybe.Count > 0)
        {
            return new DirectPlayVerdict(profile.Id, profile.Name, "maybe", [.. maybe.Select(r => $"may not play {r}")]);
        }

        return unknown
            ? new DirectPlayVerdict(profile.Id, profile.Name, "unknown", ["Weir has not measured this file yet"])
            : new DirectPlayVerdict(profile.Id, profile.Name, "yes", []);
    }
}
