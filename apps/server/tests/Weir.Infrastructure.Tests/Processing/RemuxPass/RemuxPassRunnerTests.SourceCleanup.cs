using Weir.Core.Json;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>When a finished pass must leave its source in place: a sidecar that could not travel, an output that looks incomplete.</summary>
public sealed partial class RemuxPassRunnerTests
{
    [Fact]
    public async Task A_sidecar_that_cannot_travel_keeps_the_whole_source_in_place()
    {
        var source = _folders.Source(Path.Join("Film", "film.mkv"));
        var subtitle = Path.Join(_folders.Watched, "Film", "film.en.srt");
        File.WriteAllText(subtitle, "subs");
        Directory.CreateDirectory(_folders.Out(Path.Join("Film", "film.en.srt")));

        var result = await Run("Film/film.mkv", _folders.Runtime(sidecars: ".srt"));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.True(Bool(result, "sidecar_migration_blocked"));
        Assert.False(Bool(result, "source_deleted_after_success"));
        Assert.False(Bool(result, "source_folder_deleted"));
        Assert.Contains("could not copy 1 file that was set to travel with the video", Str(result, "source_folder_skip_reason"), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(subtitle));
    }

    [Fact]
    public async Task An_output_far_smaller_than_the_source_keeps_the_source_in_place()
    {
        var source = _folders.Source(Path.Join("Film", "film.mkv"), 10_000);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var result = await Run("Film/film.mkv", runner: Runner(hardlink: false));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal("failed", Str(result, "output_completeness_check"));
        Assert.False(Bool(result, "source_deleted_after_success"));
        Assert.Equal(
            "The output file is much smaller than the source (under 1% of source size), so Weir skipped removing the release folder as a safety step.",
            Str(result, "source_folder_skip_reason"));
        Assert.True(File.Exists(source));
    }
}
