using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What a pass does when the operating system refuses a file it reads: the source is waited for, an output size is left unrecorded, a lookup failure is not the pass's.</summary>
public sealed partial class RemuxPassRunnerTests
{
    [Fact]
    public async Task A_source_whose_time_cannot_be_read_is_waited_for()
    {
        _folders.Source("film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.SharingViolation, onCall: 1, pathEndsWith: "film.mkv");

        var result = await Run("film.mkv", runner: Runner(files: files));

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.StartsWith("Weir could not read this file safely", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task A_source_that_cannot_be_rechecked_after_the_integrity_read_is_waited_for_and_nothing_is_published()
    {
        _folders.Source("film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused, onCall: 2, pathEndsWith: "film.mkv");

        var result = await Run("film.mkv", runner: Runner(files: files));

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.Contains("could not be rechecked after processing", Str(result, "reason"), StringComparison.Ordinal);
        Assert.False(File.Exists(_folders.Out("film.mkv")));
    }

    [Fact]
    public async Task A_source_that_cannot_be_rechecked_before_an_unchanged_file_is_placed_is_waited_for()
    {
        var source = _folders.Source("film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused, onCall: 3, pathEndsWith: "film.mkv");

        var result = await Run("film.mkv", runner: Runner(hardlink: false, files: files));

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.Contains("could not be rechecked after processing", Str(result, "reason"), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(_folders.Out("film.mkv")));
    }

    [Fact]
    public async Task An_unchanged_file_is_not_copied_over_an_output_the_workflow_leaves_alone()
    {
        var source = _folders.Source("film.mkv", 3000);
        File.WriteAllText(_folders.Out("film.mkv"), "already there");

        var result = await Run("film.mkv", _folders.Runtime(collision: "skip"), runner: Runner(hardlink: false));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal("skipped_by_collision_policy", Str(result, "unchanged_output_method"));
        Assert.Equal("already there", File.ReadAllText(_folders.Out("film.mkv")));
        Assert.True(File.Exists(source));
    }

    [WindowsFact("The hard-link fast path is Windows only.")]
    public async Task A_hard_linked_output_that_cannot_be_detached_from_a_surviving_source_is_removed_and_the_file_failed()
    {
        var source = _folders.Source("root.mkv", 1000);
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused, onCall: 4, pathEndsWith: "root.mkv");

        var result = await Run("root.mkv", runner: Runner(hardlink: true, files: files));

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal(RemuxPassOutcomes.FailedDuringExecution, Str(result, "outcome"));
        Assert.Contains("could not detach the validated output", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Contains("The unsafe output link was removed", Str(result, "reason"), StringComparison.Ordinal);
        Assert.False(result.ContainsKey("output_file"));
        Assert.False(File.Exists(_folders.Out("root.mkv")));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_source_whose_size_cannot_be_read_for_the_minimum_size_check_fails_before_execution()
    {
        _folders.Source("film.mkv", 5_000_000);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation, onCall: 2, pathEndsWith: "film.mkv");

        var result = await Run("film.mkv", minSizeMb: 1, runner: Runner(files: files));

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.FailedBeforeExecution, Str(result, "outcome"));
        Assert.StartsWith("Weir could not read the source file size", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task A_source_whose_age_cannot_be_read_waits_as_if_it_were_new()
    {
        _folders.Source("film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.SharingViolation, onCall: 2, pathEndsWith: "film.mkv");

        var result = await Run("film.mkv", minAge: 60, runner: Runner(files: files));

        Assert.False(Bool(result, "ok"));
        Assert.Equal(RemuxPassOutcomes.SourceNotReady, Str(result, "outcome"));
        Assert.Contains("changed too recently", Str(result, "reason"), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task An_output_whose_size_cannot_be_read_is_still_published()
    {
        _folders.Source("film.mkv", 10_000);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var outputEnd = Path.Join("out", "film.mkv");
        var files = new FaultingFileSystem()
            .Fail(FileCall.GetLength, FaultingFileSystem.Refused, onCall: 1, pathEndsWith: outputEnd)
            .Fail(FileCall.GetLength, FaultingFileSystem.Refused, onCall: 2, pathEndsWith: outputEnd);

        var result = await Run("film.mkv", runner: Runner(hardlink: false, files: files));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.True(File.Exists(_folders.Out("film.mkv")));
        Assert.True(files.Calls.Count(call => call.Call == FileCall.GetLength && call.Path.EndsWith(outputEnd, StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public async Task Original_timestamps_that_cannot_be_read_are_noted_and_the_output_is_still_placed()
    {
        _folders.Source("film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastAccessTimeUtc, FaultingFileSystem.Refused, pathEndsWith: "film.mkv");
        var runtime = _folders.Runtime(sidecars: ".srt") with { PreserveOriginalTimestamps = true };

        var result = await Run("film.mkv", runtime, runner: Runner(hardlink: false, files: files));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.StartsWith("Weir could not read the original file's timestamps", Str(result, "original_timestamps_note"), StringComparison.Ordinal);
        Assert.True(File.Exists(_folders.Out("film.mkv")));
    }

    [Fact]
    public async Task A_failed_original_language_lookup_falls_back_to_the_language_preferences()
    {
        _folders.Source("film.mkv");
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _language.Failure = new InvalidOperationException("the metadata service fell over");
        var rules = RemuxRules.DefaultConfig() with { OriginalLanguage = new OriginalLanguageRules { Enabled = true } };

        var result = await Run("film.mkv", rules: rules, runner: Runner(hardlink: false));

        Assert.True(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        var record = (WireObject)result["original_language"];
        Assert.Equal(LookupResult.StatusUnreachable, Str(record, "lookup_status"));
        Assert.Equal("The metadata lookup failed (the metadata service fell over).", Str(record, "lookup_detail"));
        Assert.Contains("0:1", Assert.Single(_media.Remuxes));
    }
}
