using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The whole pass against real ffprobe and ffmpeg (<c>WEIR_FFMPEG_DIR</c> or PATH) on tiny generated files: a remux that drops a
/// language, a file already in shape, and the damaged inputs of #494 and #539.
/// </summary>
public sealed class RemuxPassRealFfmpegTests(RealFfmpegFixtures fixtures) : IDisposable, IClassFixture<RealFfmpegFixtures>
{
    private readonly PassFolders _folders = new();
    private readonly RecordingFacts _facts = new();

    public void Dispose() => _folders.Dispose();

    private sealed class RealResolver : IMediaToolResolver
    {
        public (string Ffprobe, string Ffmpeg) Resolve() => RealFfmpeg.Tools!.Value;

        public string? ResolveMkvmerge() => RealMkvmerge.Tool;
    }

    private RemuxPassRunner Runner() =>
        new(
            new MediaTools(new ProcessRunner(), new RealResolver(), new ListLogger<MediaTools>(), TimeProvider.System),
            new RealResolver(),
            _facts,
            new FakeCleanupData(),
            new SkippedTvSeasonFolderCleanup(),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            TimeProvider.System,
            NullLogger<RemuxPassRunner>.Instance);

    /// <summary>A copy of the shared file with these audio languages, placed where the pass is told to look: the pass moves its source.</summary>
    private async Task<string> GenerateAsync(string relative, params string[] languages)
    {
        var path = Path.Join(_folders.Watched, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(await fixtures.WithAudioLanguagesAsync(languages), path);
        return path;
    }

    private Task<WireObject> Run(string relative) =>
        Runner().RunAsync(new RemuxPassRequest { Runtime = _folders.Runtime(), RelativeMediaPath = relative, RulesConfig = RemuxRules.DefaultConfig(), MinFileAgeSeconds = 0 });

    private Task<WireObject> Run(string relative, string mediaExtensionsCsv) =>
        Runner().RunAsync(new RemuxPassRequest
        {
            Runtime = _folders.Runtime() with { MediaExtensionsCsv = mediaExtensionsCsv },
            RelativeMediaPath = relative,
            RulesConfig = RemuxRules.DefaultConfig(),
            MinFileAgeSeconds = 0,
        });

    private static string Str(WireObject result, string key) => WireConvert.Str(result[key]);

    [RequiresFfmpegFact]
    public async Task A_handed_over_film_is_remuxed_without_the_unwanted_language_and_its_release_folder_removed()
    {
        var source = await GenerateAsync(Path.Join("Film.2001", "film.mkv"), "eng", "fre");

        var result = await Run("Film.2001/film.mkv");

        Assert.Equal(RemuxPassOutcomes.LiveOutputWritten, Str(result, "outcome"));
        var output = _folders.Out(Path.Join("Film.2001", "film.mkv"));
        Assert.Equal(Path.GetFullPath(output), Str(result, "output_file"));
        var probe = await new MediaTools(new ProcessRunner(), new RealResolver(), new ListLogger<MediaTools>(), TimeProvider.System).FfprobeJsonAsync(output);
        var audio = probe.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio").ToList();
        Assert.Equal("eng", Assert.Single(audio).GetProperty("tags").GetProperty("language").GetString());
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(Path.Join(_folders.Watched, "Film.2001")));
        Assert.Empty(Directory.EnumerateFiles(_folders.Work));
        Assert.Equal(2, Assert.Single(_facts.Measured).AudioTrackCount);
        Assert.Equal(JsonValueKind.Object, probe.ValueKind);
    }

    [RequiresFfmpegFact]
    public async Task A_film_already_in_shape_is_validated_and_placed_unchanged()
    {
        var source = await GenerateAsync(Path.Join("Ready", "film.mkv"), "eng");
        var bytes = await File.ReadAllBytesAsync(source);

        var result = await Run("Ready/film.mkv");

        Assert.Equal(RemuxPassOutcomes.LiveSkippedNotRequired, Str(result, "outcome"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_folders.Out(Path.Join("Ready", "film.mkv"))));
    }

    [RequiresFfmpegFact]
    public async Task A_zero_filled_mkv_is_unreadable_content_not_a_completed_copy()
    {
        var path = Path.Join(_folders.Watched, "The.Terror.1963", "The.Terror.1963.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024]);

        var result = await Run("The.Terror.1963/The.Terror.1963.mkv");

        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.Equal(RemuxPassRunner.UnreadableWait, Str(result, "not_ready_kind"));
        Assert.Equal(ToolFailureText.NotReadableYet, Str(result, "reason"));
        Assert.False(File.Exists(_folders.Out(Path.Join("The.Terror.1963", "The.Terror.1963.mkv"))));
        Assert.True(File.Exists(path));
    }

    [RequiresFfmpegFact]
    public async Task A_truncated_mkv_never_completes_as_if_it_were_whole()
    {
        var whole = await GenerateAsync(Path.Join("Cut", "whole.mkv"), "eng");
        var bytes = await File.ReadAllBytesAsync(whole);
        File.Delete(whole);
        var truncated = Path.Join(_folders.Watched, "Cut", "film.mkv");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);

        var result = await Run("Cut/film.mkv");

        Assert.NotEqual(RemuxPassOutcomes.LiveOutputWritten, Str(result, "outcome"));
        Assert.NotEqual(RemuxPassOutcomes.LiveSkippedNotRequired, Str(result, "outcome"));
        Assert.False(File.Exists(_folders.Out(Path.Join("Cut", "film.mkv"))));
        Assert.True(File.Exists(truncated));
    }

    [RequiresFfmpegFact]
    public async Task A_release_folder_holding_another_video_of_a_type_the_workflow_names_is_kept_when_the_film_is_removed()
    {
        var source = await GenerateAsync(Path.Join("Film.2001", "film.mkv"), "eng", "fre");
        var other = Path.Join(_folders.Watched, "Film.2001", "Cut.ts");
        await File.WriteAllBytesAsync(other, new byte[16]);

        var result = await Run("Film.2001/film.mkv", "mkv, ts");

        Assert.Equal(RemuxPassOutcomes.LiveOutputWritten, Str(result, "outcome"));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(other));
    }

    [RequiresFfmpegFact]
    public async Task A_release_folder_holding_a_file_of_a_type_the_workflow_does_not_name_goes_with_the_film()
    {
        var source = await GenerateAsync(Path.Join("Film.2001", "film.mkv"), "eng", "fre");
        var other = Path.Join(_folders.Watched, "Film.2001", "Cut.ts");
        await File.WriteAllBytesAsync(other, new byte[16]);

        var result = await Run("Film.2001/film.mkv", string.Empty);

        Assert.Equal(RemuxPassOutcomes.LiveOutputWritten, Str(result, "outcome"));
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(Path.Join(_folders.Watched, "Film.2001")));
    }
}
