using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What a pass does when the world changes under it: the path is not a file, a folder is missing, the file moves on, the drive fills.</summary>
public sealed partial class RemuxPassRunnerTests
{
    private const long BytesPerMegabyte = 1024L * 1024;

    /// <summary>Free space that is plentiful for the first <paramref name="plenty"/> looks and nothing after, as a drive does that fills while a file is written.</summary>
    private static Func<string, long> DriveThatFills(int plenty)
    {
        var looks = 0;
        return _ => Interlocked.Increment(ref looks) <= plenty ? 100_000 * BytesPerMegabyte : 0;
    }

    [Fact]
    public async Task A_path_that_is_a_folder_is_not_taken_for_a_media_file()
    {
        Directory.CreateDirectory(Path.Join(_folders.Watched, "Folder.mkv"));

        var result = await Run("Folder.mkv");

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.FailedBeforeExecution, Str(result, "outcome"));
        Assert.Contains("not a regular media file", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task A_custom_work_folder_that_is_missing_fails_the_pass_before_it_starts()
    {
        _folders.Source("film.mkv");
        var runtime = _folders.Runtime() with { WorkFolderEffective = Path.Join(_folders.Root, "no-such-work"), WorkFolderIsDefault = false };

        var result = await Run("film.mkv", runtime);

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.FailedBeforeExecution, Str(result, "outcome"));
        Assert.Contains("work/temp folder is missing", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Remuxes);
    }

    [Fact]
    public async Task A_file_that_changes_while_it_is_being_remuxed_is_waited_for_and_nothing_is_published()
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

        var result = await Run("film.mkv");

        Assert.True(changed, "The remux never started, so the file was never changed under it.");
        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Contains("changed while Weir was reading it", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Equal("waiting", Str(_progress[^1], "status"));
        Assert.False(File.Exists(_folders.Out("film.mkv")));
        Assert.Empty(Directory.GetFiles(_folders.Work));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task An_unchanged_file_waits_when_the_output_drive_fills_before_it_is_copied()
    {
        var source = _folders.Source("film.mkv");

        var result = await Run("film.mkv", minimumFreeMb: 100, runner: Runner(freeBytes: DriveThatFills(1)));

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal("minimum_free_disk_space", Str(result, "guardrail"));
        Assert.False(File.Exists(_folders.Out("film.mkv")));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_remuxed_file_waits_when_the_output_drive_fills_after_it_is_written()
    {
        var source = _folders.Source("film.mkv");
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var result = await Run("film.mkv", minimumFreeMb: 100, runner: Runner(freeBytes: DriveThatFills(2)));

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal("minimum_free_disk_space", Str(result, "guardrail"));
        Assert.False(File.Exists(_folders.Out("film.mkv")));
        Assert.Empty(Directory.GetFiles(_folders.Work));
        Assert.True(File.Exists(source));
    }
}
