using System.Globalization;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Processing;

internal static partial class RealMedia
{
    /// <summary>An audio track of a film to generate: its language tag, its title, and whether it is flagged as commentary.</summary>
    public sealed record AudioTrack(string Language, string Title = "", bool Commentary = false, int Channels = 2);

    /// <summary>A subtitle track of a film to generate.</summary>
    public sealed record SubtitleTrack(string Language, bool Forced = false, bool Default = false, string Title = "");

    /// <summary>What is inside a film to generate, beyond its picture.</summary>
    public sealed record Film(
        AudioTrack[] Audio,
        SubtitleTrack[] Subtitles,
        string Title = "",
        bool WithCover = false,
        bool WithAttachment = false,
        bool WithChapters = false);

    /// <summary>A track of a probed file with the flags and title a rule set can change.</summary>
    public sealed record Described(string Kind, string Language, string Title, bool Default, bool Forced, bool Commentary, int Channels);

    /// <summary>A Matroska film generated with <paramref name="film"/> inside it, the first audio track flagged default as a download usually arrives.</summary>
    public static async Task GenerateFilmAsync(string path, Film film)
    {
        using var scratch = new Harness.TemporaryFolder();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=64x64:rate=10" };
        foreach (var (track, index) in film.Audio.Select((track, index) => (track, index)))
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency={440 * (index + 1)}:duration=3"]);
        }

        var firstSubtitleInput = 1 + film.Audio.Length;
        foreach (var (_, index) in film.Subtitles.Select((track, index) => (track, index)))
        {
            var cues = Path.Combine(scratch.Path, $"cues-{index}.srt");
            await File.WriteAllTextAsync(cues, $"1\n00:00:00,100 --> 00:00:01,000\nSubtitle {index}\n");
            arguments.AddRange(["-i", cues]);
        }

        var cover = Path.Combine(scratch.Path, "cover.jpg");
        if (film.WithCover)
        {
            await RunAsync(Tool("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=32x32:rate=1:duration=1", "-frames:v", "1", cover]);
        }

        var notes = Path.Combine(scratch.Path, "notes.txt");
        await File.WriteAllTextAsync(notes, "Notes that travel inside the film.");

        if (film.WithChapters)
        {
            var chapters = Path.Combine(scratch.Path, "chapters.txt");
            await File.WriteAllTextAsync(chapters, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Two\n");
            arguments.AddRange(["-f", "ffmetadata", "-i", chapters, "-map_chapters", (firstSubtitleInput + film.Subtitles.Length).ToString(CultureInfo.InvariantCulture)]);
        }

        // Inputs are all given by now; what follows describes the file written.
        var attached = 0;
        if (film.WithCover)
        {
            arguments.AddRange(["-attach", cover, $"-metadata:s:t:{attached++}", "mimetype=image/jpeg"]);
        }

        if (film.WithAttachment)
        {
            arguments.AddRange(["-attach", notes, $"-metadata:s:t:{attached++}", "mimetype=text/plain"]);
        }

        arguments.AddRange(["-map", "0:v"]);
        for (var i = 0; i < film.Audio.Length; i++)
        {
            arguments.AddRange(["-map", $"{i + 1}:a"]);
        }

        for (var i = 0; i < film.Subtitles.Length; i++)
        {
            arguments.AddRange(["-map", $"{firstSubtitleInput + i}:s"]);
        }

        arguments.AddRange(["-c:v", "mpeg4", "-c:a", "ac3", "-c:s", "srt", "-c:t", "copy"]);
        for (var i = 0; i < film.Audio.Length; i++)
        {
            var track = film.Audio[i];
            if (track.Language.Length > 0)
            {
                arguments.AddRange([$"-metadata:s:a:{i}", $"language={track.Language}"]);
            }

            if (track.Title.Length > 0)
            {
                arguments.AddRange([$"-metadata:s:a:{i}", $"title={track.Title}"]);
            }

            arguments.AddRange([$"-ac:a:{i}", track.Channels.ToString(CultureInfo.InvariantCulture)]);

            arguments.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : track.Commentary ? "comment" : "0"]);
        }

        for (var i = 0; i < film.Subtitles.Length; i++)
        {
            var track = film.Subtitles[i];
            arguments.AddRange([$"-metadata:s:s:{i}", $"language={track.Language}"]);
            if (track.Title.Length > 0)
            {
                arguments.AddRange([$"-metadata:s:s:{i}", $"title={track.Title}"]);
            }

            var flags = string.Join('+', new[] { track.Forced ? "forced" : null, track.Default ? "default" : null }.OfType<string>());
            arguments.AddRange([$"-disposition:s:{i}", flags.Length > 0 ? flags : "0"]);
        }

        if (film.Title.Length > 0)
        {
            arguments.AddRange(["-metadata", $"title={film.Title}"]);
        }

        arguments.AddRange(["-metadata", "comment=Encoded for a contract test", path]);
        await RunAsync(Tool("ffmpeg"), arguments);
    }

    /// <summary>Every track of the file with the flags and title a rule set can change, in the order ffprobe lists them.</summary>
    public static async Task<IReadOnlyList<Described>> DescribeAsync(string path) =>
        [.. (await ProbeAsync(path))["streams"]!.AsArray().Select(stream =>
        {
            var disposition = stream!["disposition"];
            return new Described(
                (string)stream["codec_type"]!,
                LanguageOrEmpty((string?)stream["tags"]?["language"]),
                (string?)stream["tags"]?["title"] ?? string.Empty,
                (int?)disposition?["default"] == 1,
                (int?)disposition?["forced"] == 1,
                (int?)disposition?["comment"] == 1,
                (int?)stream["channels"] ?? 0);
        })];

    /// <summary>The title tag of the film itself, empty when it has none.</summary>
    public static async Task<string> FilmTitleAsync(string path) => await FilmTagAsync(path, "title");

    /// <summary>The "comment" tag of the film itself, which a rule set can strip with the other metadata.</summary>
    public static async Task<string> FilmCommentAsync(string path) => await FilmTagAsync(path, "comment");

    /// <summary>A container tag by name, however the container spells its case (Matroska writes them in capitals).</summary>
    private static async Task<string> FilmTagAsync(string path, string name) =>
        (await ProbeAsync(path))["format"]!["tags"]?.AsObject()
            .FirstOrDefault(tag => string.Equals(tag.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>() ?? string.Empty;

    public static async Task<int> ChapterCountAsync(string path) =>
        JsonNode.Parse(await RunAsync(Tool("ffprobe"), ["-v", "error", "-print_format", "json", "-show_chapters", path]))!["chapters"]!.AsArray().Count;
}
