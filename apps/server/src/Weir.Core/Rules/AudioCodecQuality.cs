using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>Audio codec ranking, lossless detection and the container allowlist used when planning a remux.</summary>
public static partial class RemuxRules
{
    /// <summary>Best (index 0) to worst. Unknown codecs sort after all of these.</summary>
    public static IReadOnlyList<string> AudioCodecQualityOrder { get; } =
    [
        "truehd", "dts_hd_ma", "flac", "alac", "pcm_s32le", "pcm_s24le", "pcm_s16le", "pcm_f32le", "pcm_u8", "wavpack",
        "opus", "libopus", "eac3", "ac3", "dca", "dts", "aac", "libfdk_aac", "mp2", "mp3", "vorbis", "libvorbis", "wmav2",
    ];

    public const int CodecUnknownRank = 23 + 32;

    private static readonly Dictionary<string, int> CodecRankLookup =
        AudioCodecQualityOrder.Select((codec, rank) => (codec, rank)).ToDictionary(p => p.codec, p => p.rank, StringComparer.Ordinal);

    private static readonly HashSet<string> LosslessCodecs =
        new(StringComparer.Ordinal) { "flac", "truehd", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le", "wavpack" };

    /// <summary>
    /// Containers Processing can genuinely process. Raw elementary streams (<c>.h264</c>, <c>.h265</c>,
    /// <c>.mpv</c>) stay out: they have no audio, so a plan fails and failure cleanup would delete the folder.
    /// </summary>
    public static IReadOnlySet<string> MediaExtensions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ".mkv", ".mp4", ".m4v", ".webm", ".avi", ".mpe", ".mpeg", ".mpg", ".mov", ".flv", ".wmv", ".avchd",
    };

    /// <summary>The effective allowlist, for operator-facing reporting.</summary>
    public static IReadOnlyList<string> MediaExtensionsSorted() => [.. MediaExtensions.Order(StringComparer.Ordinal)];

    /// <summary>Lower rank is the better codec.</summary>
    public static int AudioCodecQualityRank(string? codecName)
    {
        var c = RulesJson.Lower(WireStrings.Strip(codecName ?? string.Empty));
        if (c.Length == 0)
        {
            return CodecUnknownRank;
        }

        return CodecRankLookup.TryGetValue(c, out var rank) ? rank : CodecUnknownRank;
    }

    private static bool IsLosslessAudio(string codecName) => LosslessCodecs.Contains(RulesJson.Lower(WireStrings.Strip(codecName)));
}
