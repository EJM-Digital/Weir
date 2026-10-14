using System.Globalization;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Processing;

internal static partial class RealMedia
{
    /// <summary>One track of a file, as ffprobe reports it.</summary>
    public sealed record Track(string Kind, string Language, string Codec);

    /// <summary>
    /// A short test video with one audio track per entry of <paramref name="audioLanguages"/> and one subtitle track per entry of
    /// <paramref name="subtitleLanguages"/> (none of them marked default), in the container the extension of <paramref name="path"/> names.
    /// An empty language leaves the track untagged. <paramref name="firstAudioDefault"/> flags the first audio track as the default one, which
    /// is how a file that already follows the library's rules arrives.
    /// </summary>
    public static async Task GenerateAsync(
        string path, int seconds, string[] audioLanguages, string[] subtitleLanguages, bool firstAudioDefault = false)
    {
        var isMp4 = string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
        using var scratch = new Harness.TemporaryFolder();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=64x64:rate=10" };
        for (var i = 0; i < audioLanguages.Length; i++)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency={440 * (i + 1)}:duration={seconds}"]);
        }

        for (var i = 0; i < subtitleLanguages.Length; i++)
        {
            var cues = Path.Combine(scratch.Path, $"cues-{i}.srt");
            await File.WriteAllTextAsync(cues, $"1\n00:00:00,100 --> 00:00:01,000\nSubtitle {i}\n");
            arguments.AddRange(["-i", cues]);
        }

        arguments.AddRange(["-map", "0:v"]);
        for (var i = 0; i < audioLanguages.Length; i++)
        {
            arguments.AddRange(["-map", $"{i + 1}:a"]);
        }

        for (var i = 0; i < subtitleLanguages.Length; i++)
        {
            arguments.AddRange(["-map", $"{audioLanguages.Length + i + 1}:s"]);
        }

        arguments.AddRange(["-c:v", "mpeg4", "-c:a", isMp4 ? "aac" : "mp2", "-c:s", isMp4 ? "mov_text" : "srt"]);
        for (var i = 0; i < audioLanguages.Length; i++)
        {
            if (audioLanguages[i].Length > 0)
            {
                arguments.AddRange([$"-metadata:s:a:{i}", $"language={audioLanguages[i]}"]);
            }
        }

        if (firstAudioDefault && audioLanguages.Length > 0)
        {
            arguments.AddRange(["-disposition:a:0", "default"]);
        }

        for (var i = 0; i < subtitleLanguages.Length; i++)
        {
            arguments.AddRange([$"-disposition:s:{i}", "0"]);
            if (subtitleLanguages[i].Length > 0)
            {
                arguments.AddRange([$"-metadata:s:s:{i}", $"language={subtitleLanguages[i]}"]);
            }
        }

        arguments.Add(path);
        await RunAsync(Tool("ffmpeg"), arguments);
    }

    /// <summary>Every track of the file in the order ffprobe lists them.</summary>
    public static async Task<IReadOnlyList<Track>> TracksAsync(string path) =>
        [.. (await ProbeAsync(path))["streams"]!.AsArray().Select(stream => new Track(
            (string)stream!["codec_type"]!,
            LanguageOrEmpty((string?)stream["tags"]?["language"]),
            (string)stream["codec_name"]!))];

    /// <summary>A track with no language tag reads as empty, however the container spells "undetermined".</summary>
    private static string LanguageOrEmpty(string? language) => language is null or "und" ? string.Empty : language;

    /// <summary>The length of the file in seconds, as its container reports it.</summary>
    public static async Task<double> DurationSecondsAsync(string path) =>
        double.Parse((string)(await ProbeAsync(path))["format"]!["duration"]!, CultureInfo.InvariantCulture);

    /// <summary>
    /// The program that wrote the Matroska file, as the statistics tag on its first track names it. Only mkvmerge writes that tag, so an
    /// empty answer means another program wrote the file.
    /// </summary>
    public static async Task<string> MkvmergeVersionAsync(string path) =>
        (string?)(await ProbeAsync(path))["streams"]![0]!["tags"]?["_STATISTICS_WRITING_APP"] ?? string.Empty;

    private static async Task<JsonNode> ProbeAsync(string path) =>
        JsonNode.Parse(await RunAsync(Tool("ffprobe"), ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path]))!;

    /// <summary>Reads the whole file with ffmpeg, which fails when the file cannot be decoded to its end.</summary>
    public static async Task AssertPlaysThroughAsync(string path) =>
        await RunAsync(Tool("ffmpeg"), ["-v", "error", "-xerror", "-i", path, "-f", "null", "-"]);
}
