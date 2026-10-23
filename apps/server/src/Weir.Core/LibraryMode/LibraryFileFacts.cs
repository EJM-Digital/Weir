using System.Globalization;
using Weir.Core.Rules;

namespace Weir.Core.LibraryMode;

/// <summary>
/// The facet names the Library view (#568) groups a library's files by. Stored verbatim in
/// <c>library_file_facets.facet</c> and accepted verbatim as the Files listing's facet filters, so they are a
/// closed, stable vocabulary rather than free text.
/// </summary>
public static class LibraryFacets
{
    /// <summary>The file's video codec, e.g. <c>hevc</c>. One value per file.</summary>
    public const string VideoCodec = "video_codec";

    /// <summary>The file's resolution class, one of <see cref="LibraryFileFacts.ResolutionClasses"/>. One value per file.</summary>
    public const string Resolution = "resolution";

    /// <summary>An audio codec and its channel layout together, e.g. <c>eac3 5.1</c>. One value per distinct audio track shape.</summary>
    public const string AudioCodecChannels = "audio";

    /// <summary>A language an audio track is tagged with, canonicalised, e.g. <c>eng</c>.</summary>
    public const string AudioLanguage = "audio_language";

    /// <summary>A language a subtitle track is tagged with, canonicalised, e.g. <c>fre</c>.</summary>
    public const string SubtitleLanguage = "subtitle_language";

    /// <summary>Every facet the aggregate and filter endpoints accept, in the order the Library view shows them.</summary>
    public static readonly IReadOnlyList<string> All = [VideoCodec, Resolution, AudioCodecChannels, AudioLanguage, SubtitleLanguage];

    public static bool IsKnown(string? facet) => facet is not null && All.Contains(facet, StringComparer.Ordinal);
}

/// <summary>One <c>(facet, value)</c> pair a file contributes to the breakdowns. A file contributes each pair at most once.</summary>
public sealed record LibraryFileFacet(string Facet, string Value);

/// <summary>
/// The codec, resolution and language facts the Library view (#568) shows and groups by, read out of the ffprobe
/// JSON a scan already cached on the file's row. Every field can be <see cref="LibraryFileFacts.Unknown"/> or
/// zero: issue #568 is explicit that a fact the cache does not carry is reported as "unknown" rather than
/// triggering a fresh probe.
/// </summary>
public sealed record LibraryFileFacts(
    string VideoCodec,
    int? VideoHeight,
    string ResolutionClass,
    int AudioTrackCount,
    int SubtitleTrackCount,
    string? AudioSummary,
    string? SubtitleSummary,
    IReadOnlyList<LibraryFileFacet> Facets)
{
    /// <summary>The value every facet uses when the cached probe does not say. Never a guess, never a blank cell.</summary>
    public const string Unknown = "unknown";

    /// <summary>What a file with no usable ffprobe JSON reports: everything unknown, nothing else to group by.</summary>
    public static readonly LibraryFileFacts Nothing = new(
        Unknown,
        null,
        Unknown,
        0,
        0,
        null,
        null,
        [new LibraryFileFacet(LibraryFacets.VideoCodec, Unknown), new LibraryFileFacet(LibraryFacets.Resolution, Unknown)]);

    /// <summary>The resolution classes, largest first, as the breakdown orders them.</summary>
    public static readonly IReadOnlyList<string> ResolutionClasses = ["4k", "1080p", "720p", "sd", Unknown];
}

/// <summary>
/// Derives <see cref="LibraryFileFacts"/> from one file's cached ffprobe JSON. Pure: it reads the probe document and
/// nothing else, so a scan, a migration back-fill and a test all agree about what a given probe means.
/// </summary>
/// <remarks>
/// <para>The SQL back-fill in migration <c>0007_library_file_facets.sql</c> mirrors these rules for rows scanned
/// before #568 added the columns, so an existing install gets a populated Library view without waiting for a
/// rescan. <c>LibraryFileFacetsMigrationTests</c> compares the two on the same probe JSON, which is what keeps them from
/// drifting; a change here needs the same change there.</para>
/// </remarks>
public static class LibraryFileFactsReader
{
    private const int SummaryTrackLimit = 3;

    /// <summary>Reads every fact from a probe document, or <see cref="LibraryFileFacts.Unknown"/> when there is none.</summary>
    public static LibraryFileFacts Derive(string? probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson))
        {
            return LibraryFileFacts.Nothing;
        }

        try
        {
            return Derive(ProbeResult.Parse(probeJson));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return LibraryFileFacts.Nothing;
        }
    }

    public static LibraryFileFacts Derive(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var streams = probe.Streams;
        var video = streams.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.Ordinal) && !IsCoverArt(s));
        var audio = streams.Where(s => string.Equals(s.CodecType, "audio", StringComparison.Ordinal)).ToList();
        var subtitles = streams.Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.Ordinal)).ToList();

        var videoCodec = NormalizeCodec(video?.CodecName);
        var height = IntTag(video, "height");
        var width = IntTag(video, "width");
        var resolution = ResolutionClassFor(width, height);

        var facets = new List<LibraryFileFacet>
        {
            new(LibraryFacets.VideoCodec, videoCodec),
            new(LibraryFacets.Resolution, resolution),
        };

        var audioShapes = new List<string>();
        foreach (var stream in audio)
        {
            var shape = AudioShapeFor(stream);
            audioShapes.Add(shape);
            Add(facets, LibraryFacets.AudioCodecChannels, shape);
            Add(facets, LibraryFacets.AudioLanguage, LanguageOf(stream));
        }

        var subtitleLanguages = new List<string>();
        foreach (var stream in subtitles)
        {
            var language = LanguageOf(stream);
            subtitleLanguages.Add(language);
            Add(facets, LibraryFacets.SubtitleLanguage, language);
        }

        return new LibraryFileFacts(
            videoCodec,
            height,
            resolution,
            audio.Count,
            subtitles.Count,
            SummaryOf(audio.Select((stream, i) => $"{LanguageOf(stream)} {audioShapes[i]}")),
            SummaryOf(subtitleLanguages),
            facets);
    }

    /// <summary>
    /// The resolution class a width/height falls into. Judged on height, with a width fallback so a 3840×1600
    /// scope-ratio release is still 4K rather than 1080p; a probe with neither is <see cref="LibraryFileFacts.Unknown"/>.
    /// </summary>
    public static string ResolutionClassFor(int? width, int? height)
    {
        var h = height ?? 0;
        var w = width ?? 0;
        if (h >= 1700 || w >= 3000)
        {
            return "4k";
        }

        if (h >= 1000 || w >= 1800)
        {
            return "1080p";
        }

        if (h >= 700 || w >= 1200)
        {
            return "720p";
        }

        return h > 0 || w > 0 ? "sd" : LibraryFileFacts.Unknown;
    }

    /// <summary>
    /// A channel layout an operator reads: ffprobe's own <c>channel_layout</c> with any "(side)"/"(back)" qualifier
    /// dropped, or the plain channel count mapped to the names those counts always mean.
    /// </summary>
    public static string ChannelLayoutFor(string? channelLayout, int? channels)
    {
        var layout = (channelLayout ?? string.Empty).Trim();
        if (layout.Length > 0)
        {
            var qualifier = layout.IndexOf('(', StringComparison.Ordinal);
            if (qualifier > 0)
            {
                layout = layout[..qualifier];
            }

            return layout.ToLowerInvariant();
        }

        return channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            > 0 => channels.Value.ToString(CultureInfo.InvariantCulture) + "ch",
            _ => LibraryFileFacts.Unknown,
        };
    }

    /// <summary>One audio track's facet value: its codec and channel layout together, e.g. <c>eac3 5.1</c>.</summary>
    public static string AudioShapeFor(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var codec = NormalizeCodec(stream.CodecName);
        var layout = ChannelLayoutFor(StringTag(stream, "channel_layout"), IntTag(stream, "channels"));
        return $"{codec} {layout}";
    }

    /// <summary>A track's canonical language code, or <see cref="LibraryFileFacts.Unknown"/> when it carries none.</summary>
    public static string LanguageOf(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var code = OriginalLanguage.CanonicalLanguage(stream.Tag("language"));
        return code.Length == 0 || string.Equals(code, "und", StringComparison.Ordinal) ? LibraryFileFacts.Unknown : code;
    }

    private static string NormalizeCodec(string? codecName)
    {
        var codec = (codecName ?? string.Empty).Trim().ToLowerInvariant();
        return codec.Length == 0 ? LibraryFileFacts.Unknown : codec;
    }

    /// <summary>
    /// A cover-art / poster stream is a video stream as far as ffprobe is concerned, so the file's real video codec
    /// would otherwise come out as <c>mjpeg</c> whenever the poster happens to be listed first.
    /// </summary>
    private static bool IsCoverArt(ProbeStreamInfo stream) =>
        stream.Disposition.TryGetValue("attached_pic", out var attached) && attached != 0;

    private static void Add(List<LibraryFileFacet> facets, string facet, string value)
    {
        if (!facets.Any(f => string.Equals(f.Facet, facet, StringComparison.Ordinal) && string.Equals(f.Value, value, StringComparison.Ordinal)))
        {
            facets.Add(new LibraryFileFacet(facet, value));
        }
    }

    private static string? SummaryOf(IEnumerable<string> parts)
    {
        var all = parts.ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var shown = all.Take(SummaryTrackLimit).ToList();
        var extra = all.Count - shown.Count;
        return extra > 0
            ? string.Join(", ", shown) + $" +{extra.ToString(CultureInfo.InvariantCulture)} more"
            : string.Join(", ", shown);
    }

    private static int? IntTag(ProbeStreamInfo? stream, string name) =>
        stream?.Get(name) is { } value && RulesJson.TryInt(value, out var number) ? (int)number : null;

    private static string? StringTag(ProbeStreamInfo stream, string name) =>
        stream.Get(name) is { } value && RulesJson.IsStr(value) ? value.GetString() : null;
}
