using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>A pass nobody is watching (no progress reporter) fails and waits exactly as one that is watched, and the results it builds leave out what it was not told.</summary>
public sealed partial class RemuxPassRunnerTests
{
    [Fact]
    public async Task A_file_with_no_extension_is_refused_without_naming_one()
    {
        _folders.Source("README");

        var result = await Run("README");

        Assert.False(Bool(result, "ok"));
        Assert.StartsWith("Weir does not process this files in this pass.", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task An_unchanged_file_that_cannot_be_placed_is_failed_with_no_one_listening()
    {
        var source = _folders.Source(Path.Join("Film", "film.mkv"));
        var inTheWay = _folders.Out(Path.Join("Film", "film.mkv"));
        Directory.CreateDirectory(inTheWay);
        File.WriteAllText(Path.Join(inTheWay, "keep.txt"), "someone else's file");

        var result = await Run("Film/film.mkv", withProgress: false);

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.FailedDuringExecution, Str(result, "outcome"));
        Assert.True(File.Exists(source));
        Assert.Empty(_progress);
    }

    [Fact]
    public async Task A_remuxed_file_that_cannot_be_placed_is_failed_with_no_one_listening()
    {
        var source = _folders.Source(Path.Join("Film", "film.mkv"), 10_000);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var inTheWay = _folders.Out(Path.Join("Film", "film.mkv"));
        Directory.CreateDirectory(inTheWay);
        File.WriteAllText(Path.Join(inTheWay, "keep.txt"), "someone else's file");

        var result = await Run("Film/film.mkv", runner: Runner(hardlink: false), withProgress: false);

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal(RemuxPassOutcomes.FailedDuringExecution, Str(result, "outcome"));
        Assert.True(File.Exists(source));
        Assert.Empty(_progress);
    }

    [Fact]
    public async Task A_file_that_changes_while_it_is_being_remuxed_is_waited_for_with_no_one_listening()
    {
        var source = _folders.Source("film.mkv");
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var changed = false;
        _media.OnCall = () =>
        {
            if (!changed && _media.Remuxes.Any())
            {
                changed = true;
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-5));
            }
        };

        var result = await Run("film.mkv", withProgress: false);

        Assert.True(changed);
        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.Empty(_progress);
    }

    [Fact]
    public void A_result_leaves_out_the_path_it_inspected_when_it_was_not_told_one()
    {
        Assert.False(RemuxPassRunner.SourceNotReady("a.mkv", "wait").ContainsKey("inspected_source_path"));
        Assert.False(RemuxPassRunner.SourceNotReady("a.mkv", "wait", string.Empty).ContainsKey("inspected_source_path"));
        Assert.Equal("/in/a.mkv", Str(RemuxPassRunner.SourceNotReady("a.mkv", "wait", "/in/a.mkv"), "inspected_source_path"));
        Assert.False(RemuxPassRunner.SourceGone("a.mkv", null).ContainsKey("inspected_source_path"));
        Assert.False(RemuxPassRunner.SourceGone("a.mkv", string.Empty).ContainsKey("inspected_source_path"));
        Assert.Equal("/in/a.mkv", Str(RemuxPassRunner.SourceGone("a.mkv", "/in/a.mkv"), "inspected_source_path"));
        Assert.False(RemuxPassRunner.SkipGuardrail("a.mkv", "small", "minimum_input_file_size", null, new WireObject()).ContainsKey("inspected_source_path"));
    }
}
