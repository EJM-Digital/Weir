using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Jobs;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>A swap over the in-memory filesystem: one library file, "OLD", replaced by a cleaned copy, "NEW".</summary>
internal sealed class SwapScenario
{
    public const string Library = "/lib";
    public const string Original = "/lib/Movie (2020)/Movie (2020).mkv";
    public const long JobId = 42;

    public static readonly string Temp = SafeSwapRules.TempPath(Original);
    public static readonly string Backup = SafeSwapRules.BackupPath(Original);

    public SwapScenario()
    {
        Files.Put(Original, "OLD");
        Journal = new FakeSwapJournal(Files);
        Swap = new SafeSwap(Files, Journal, Validator, SwapLogger);
        Sweep = new SwapRecoverySweep(Files, Journal, ActivityWriter, SweepLogger);
    }

    public FakeSwapFileSystem Files { get; } = new();

    public FakeSwapJournal Journal { get; }

    public FakeValidator Validator { get; } = new();

    public ListLogger<SafeSwap> SwapLogger { get; } = new();

    public ListLogger<SwapRecoverySweep> SweepLogger { get; } = new();

    public RecordingActivityWriter ActivityWriter { get; } = new();

    public SafeSwap Swap { get; }

    public SwapRecoverySweep Sweep { get; }

    public List<string> WrittenTo { get; } = [];

    /// <summary>What the "ffmpeg" of the scenario does. Defaults to writing NEW.</summary>
    public Func<string, CancellationToken, Task>? Writer { get; set; }

    public Task<SwapResult> RunAsync(SwapOptions? options = null, CancellationToken cancellationToken = default) =>
        Swap.RunAsync(
            JobId,
            Original,
            (temp, token) =>
            {
                WrittenTo.Add(temp);
                if (Writer is not null)
                {
                    return Writer(temp, token);
                }

                Files.Write(temp, "NEW");
                return Task.CompletedTask;
            },
            options,
            cancellationToken);
}

public sealed class SafeSwapTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;

    [Fact]
    public async Task A_clean_swap_puts_the_cleaned_copy_under_the_original_name_and_leaves_nothing_else()
    {
        var s = new SwapScenario();
        s.Files.SetPermissions(Original, "rw-r-----");
        var before = s.Files.Fingerprint(Original);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal(SafeSwapRules.CommittedMessage, result.Message);
        Assert.True(result.BackupRemoved);
        Assert.Empty(result.Warnings);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.Equal("rw-r-----", s.Files.PermissionsOf(Original));
        Assert.NotEqual(before.ModifiedTimeNs, s.Files.Fingerprint(Original).ModifiedTimeNs);
        Assert.Equal(["/lib/Movie (2020)/Movie (2020).weir-tmp.mkv"], s.WrittenTo);
        Assert.Equal([Temp], s.Validator.Checked);
        Assert.Equal(
            [SwapJournalState.Writing, SwapJournalState.Committing, SwapJournalState.Committed, SwapJournalState.Finished],
            s.Journal.History.Select(entry => entry.State));
        Assert.All(s.Journal.History, entry => Assert.Equal((SwapScenario.JobId, Original), (entry.JobId, entry.OriginalPath)));
    }

    [Fact]
    public async Task The_steps_run_in_the_documented_order()
    {
        var s = new SwapScenario();
        await s.RunAsync();

        Assert.Equal(
            [
                $"FileExists({Backup})", $"FileExists({Temp})", $"FileExists({Temp})", $"FileExists({Backup})",
                $"FileExists({Original})", $"Fingerprint({Original})", $"LinkCount({Original})",
                $"AvailableFreeBytes({Path.GetDirectoryName(Original)})", $"ProbeWrite({Path.GetDirectoryName(Original)})",
                $"IsReadOnly({Original})", $"IsInUse({Original})",
                "Journal(Writing)",
                $"FileExists({Temp})", $"FileExists({Original})", $"Fingerprint({Original})",
                $"CopyPermissions({Original} -> {Temp})",
                "Journal(Committing)",
                $"Move({Original} -> {Backup})", $"Fingerprint({Backup})",
                $"Move({Temp} -> {Original})",
                "Journal(Committed)",
                $"Delete({Backup})",
                "Journal(Finished)",
            ],
            s.Files.Operations);
    }

    /// <summary>Every operation of a clean swap, for every fault mode.</summary>
    public static TheoryData<FaultMode, int, string> EveryStep()
    {
        var clean = new SwapScenario();
        clean.RunAsync().GetAwaiter().GetResult();
        var data = new TheoryData<FaultMode, int, string>();
        foreach (var mode in Enum.GetValues<FaultMode>())
        {
            for (var index = 0; index < clean.Files.Operations.Count; index++)
            {
                data.Add(mode, index, clean.Files.Operations[index]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStep))]
    public async Task A_failure_or_crash_at_any_step_leaves_exactly_one_intact_file(FaultMode mode, int index, string operation)
    {
        var s = new SwapScenario();
        s.Files.FaultAt = index;
        s.Files.Mode = mode;

        var result = await s.RunAsync();

        Assert.Equal(operation, s.Files.Operations[index]);
        // The commit is the rename of the cleaned copy onto the original name, and nothing else.
        var committed = s.Files.Effects.Contains($"Move({Temp} -> {Original})");
        var expected = committed ? "NEW" : "OLD";

        if (mode is FaultMode.Throw or FaultMode.ThrowAfterEffect)
        {
            // The same process carried on: the original name already holds intact content, without any sweep.
            Assert.True(s.Files.Has(Original), $"{mode} at {operation}: the original name is empty");
            Assert.Equal(expected, s.Files.Read(Original));
            if (mode == FaultMode.Throw)
            {
                Assert.Equal(committed, result.Committed);
            }

            if (!result.Committed)
            {
                Assert.False(s.Files.Has(Temp), $"{mode} at {operation}: the temp file was left behind");
            }
        }
        else
        {
            Assert.True(s.Files.Crashed);
        }

        // The next start: the sweep over the journal alone (no folder walk) puts everything right.
        s.Files.Restart();
        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(0, report.Problems);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal(expected, s.Files.Read(Original));
        Assert.Empty(await s.Journal.ListUnfinishedAsync());

        // A folder walk afterwards finds nothing more to do.
        var walk = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);
        Assert.Equal(SwapRecoveryReport.Empty, walk);
    }

    [Fact]
    public async Task The_fault_matrix_covers_every_mutation_of_the_swap()
    {
        var data = EveryStep();
        Assert.Equal(Enum.GetValues<FaultMode>().Length * 23, data.Count);
        var operations = data.Select(row => (string)row[2]).Distinct().ToList();
        Assert.Contains($"Move({Original} -> {Backup})", operations);
        Assert.Contains($"Move({Temp} -> {Original})", operations);
        Assert.Contains($"Delete({Backup})", operations);
        Assert.Contains("Journal(Committed)", operations);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_change_to_the_original_during_the_write_replaces_nothing()
    {
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NEW");
            s.Files.Write(Original, "MANAGER IMPORTED A NEWER COPY");
            return Task.CompletedTask;
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Equal("The file changed while Weir was working; nothing was replaced", result.Message);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("MANAGER IMPORTED A NEWER COPY", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task A_change_to_the_original_during_validation_replaces_nothing()
    {
        var s = new SwapScenario();
        s.Validator.During = () => s.Files.Write(Original, "OLD, appended to");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD, appended to", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_replaced_original_with_the_same_size_is_caught_by_its_identity()
    {
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NEW");
            s.Files.Put(Original, "OLD"); // a different file (new inode, new mtime) with the same size
            return Task.CompletedTask;
        };

        Assert.Equal(SwapOutcome.SourceChanged, (await s.RunAsync()).Outcome);
        Assert.Equal([Original], s.Files.Paths);
    }

    [Fact]
    public async Task A_deleted_original_is_not_replaced_by_the_cleaned_copy()
    {
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NEW");
            s.Files.Delete(Original);
            return Task.CompletedTask;
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Empty(s.Files.Paths);
    }

    [Fact]
    public async Task A_write_to_the_original_as_it_is_moved_aside_is_caught_and_the_original_put_back()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == $"Fingerprint({Backup})")
            {
                s.Files.Write(Backup, "OLD plus bytes a writer added through its open handle");
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD plus bytes a writer added through its open handle", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_newer_file_that_appears_under_the_original_name_mid_commit_is_never_overwritten()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == $"Move({Temp} -> {Original})")
            {
                s.Files.Put(Original, "MANAGER");
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("MANAGER", s.Files.Read(Original));
    }

    [Fact]
    public async Task Output_that_fails_validation_is_discarded_and_the_original_kept()
    {
        var s = new SwapScenario();
        s.Validator.Answer = SwapValidation.Fail("the output is 40 minutes shorter than the source");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(
            "The cleaned copy did not pass Weir's output checks, so the original was kept: the output is 40 minutes shorter than the source",
            result.Message);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_validator_that_throws_rolls_back()
    {
        var s = new SwapScenario();
        s.Validator.Throw = new InvalidOperationException("ffprobe crashed");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Contains("ffprobe crashed", result.Message, StringComparison.Ordinal);
        Assert.Equal([Original], s.Files.Paths);
    }

    [Fact]
    public async Task A_writer_that_fails_part_way_leaves_no_temp_file()
    {
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NE");
            throw new IOException("No space left on device");
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal(
            "Weir could not finish replacing the file while writing the cleaned copy, so the original was kept. The system reported: No space left on device",
            result.Message);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_writer_that_writes_nothing_fails_without_touching_the_original()
    {
        var s = new SwapScenario { Writer = (_, _) => Task.CompletedTask };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Empty(s.Validator.Checked);
    }

    [Fact]
    public async Task Cancellation_during_the_write_rolls_back_and_rethrows()
    {
        using var cancel = new CancellationTokenSource();
        var s = new SwapScenario();
        s.Writer = (temp, token) =>
        {
            s.Files.Write(temp, "NE");
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(cancellationToken: cancel.Token));

        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task A_hardlinked_file_is_skipped_before_any_work()
    {
        var s = new SwapScenario();
        s.Files.Put(Original, "OLD", links: 2);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Hardlinked, result.Outcome);
        Assert.Equal(SafeSwapRules.HardlinkedMessage, result.Message);
        Assert.Empty(s.WrittenTo);
        Assert.Empty(s.Journal.History);
        Assert.Equal([Original], s.Files.Paths);
    }

    [Fact]
    public async Task A_hardlinked_file_is_replaced_when_the_library_allows_it()
    {
        var s = new SwapScenario();
        s.Files.Put(Original, "OLD", links: 2);

        var result = await s.RunAsync(new SwapOptions(AllowHardlinked: true));

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task An_unknown_link_count_is_noted_but_does_not_block()
    {
        var s = new SwapScenario();
        s.Files.LinkCountUnknown = true;

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Contains("Weir could not check whether this file is hardlinked on this platform.", result.Warnings);
    }

    [Fact]
    public async Task Insufficient_space_is_refused_and_exactly_enough_is_allowed()
    {
        var s = new SwapScenario();
        var keepFree = new SwapOptions(KeepFreeBytes: 2L << 30);
        s.Files.FreeBytes = 3 + keepFree.KeepFreeBytes - 1;

        var refused = await s.RunAsync(keepFree);

        Assert.Equal(SwapOutcome.InsufficientSpace, refused.Outcome);
        Assert.Equal(
            "Waiting: the drive this file is on has less than 2.0 GB free (2.0 GB free now), which is the file's size plus the 2.0 GB this workflow keeps free. Nothing was changed. Weir tries again when there is room.",
            refused.Message);
        Assert.Empty(s.WrittenTo);

        s.Files.FreeBytes = 3 + keepFree.KeepFreeBytes;
        Assert.Equal(SwapOutcome.Committed, (await s.RunAsync(keepFree)).Outcome);
    }

    [Fact]
    public async Task A_folder_Weir_cannot_write_to_is_refused()
    {
        var s = new SwapScenario();
        s.Files.WriteProblem = "Access to the path is denied.";

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.NotWritable, result.Outcome);
        Assert.Equal("Weir cannot write to this file's folder, so it did not start. The system reported: Access to the path is denied.", result.Message);
        Assert.Empty(s.WrittenTo);
    }

    [Fact]
    public async Task A_read_only_file_is_refused()
    {
        var s = new SwapScenario();
        s.Files.SetReadOnly(Original);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.NotWritable, result.Outcome);
        Assert.Equal(SafeSwapRules.ReadOnlyMessage, result.Message);
    }

    [Fact]
    public async Task A_missing_file_is_reported_as_missing()
    {
        var s = new SwapScenario();
        s.Files.Delete(Original);

        Assert.Equal(SwapOutcome.SourceMissing, (await s.RunAsync()).Outcome);
    }

    [Fact]
    public async Task A_file_in_use_before_the_work_is_an_in_use_result_not_a_failure()
    {
        var s = new SwapScenario();
        s.Files.SetInUse(Original);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.InUse, result.Outcome);
        Assert.Equal(SafeSwapRules.InUseMessage, result.Message);
        Assert.Empty(s.WrittenTo);
    }

    [Fact]
    public async Task A_file_locked_at_the_commit_is_an_in_use_result_with_the_original_untouched()
    {
        var s = new SwapScenario();
        s.Writer = (temp, _) =>
        {
            s.Files.Write(temp, "NEW");
            s.Files.LockedForRename.Add(Original); // playback started while Weir was writing
            return Task.CompletedTask;
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.InUse, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.RolledBack, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.Contains(s.SwapLogger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("in use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_backup_that_cannot_be_deleted_is_left_for_the_sweep_and_the_swap_still_commits()
    {
        var s = new SwapScenario();
        s.Files.LockedForRename.Add(Backup);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.False(result.BackupRemoved);
        Assert.Single(result.Warnings);
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.True(s.Files.Has(Backup));
        Assert.Equal(SwapJournalState.Committed, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.Contains(s.SwapLogger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("startup sweep", StringComparison.Ordinal));

        s.Files.LockedForRename.Clear();
        var report = await s.Sweep.RunAsync([], walkFolders: false);

        Assert.Equal(1, report.BackupsDeleted);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_journal_that_cannot_record_the_commit_does_not_undo_it()
    {
        var s = new SwapScenario();
        s.Files.OnStep = operation =>
        {
            if (operation == "Journal(Committed)")
            {
                s.Files.FaultAt = s.Files.Operations.Count - 1;
            }
        };

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("Weir replaced the file but could not record it on the job", StringComparison.Ordinal));
        Assert.Equal([Original], s.Files.Paths);
    }

    [Fact]
    public async Task Leftovers_of_an_earlier_swap_of_the_same_file_are_put_right_first()
    {
        var s = new SwapScenario();
        s.Files.Put(Backup, "an old backup");
        s.Files.Put(Temp, "an old temp");

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task Leftovers_that_cannot_be_removed_stop_the_swap_before_any_work()
    {
        var s = new SwapScenario();
        s.Files.Put(Backup, "an old backup");
        s.Files.LockedForRename.Add(Backup);

        var result = await s.RunAsync();

        Assert.Equal(SwapOutcome.Failed, result.Outcome);
        Assert.Empty(s.WrittenTo);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public void Preflight_changes_nothing()
    {
        var s = new SwapScenario();
        var preflight = s.Swap.Preflight(Original);

        Assert.True(preflight.Ready);
        Assert.Equal(Temp, preflight.TempPath);
        Assert.Equal(Backup, preflight.BackupPath);
        Assert.DoesNotContain(
            s.Files.Effects,
            effect => effect.StartsWith("Move", StringComparison.Ordinal) || effect.StartsWith("Delete", StringComparison.Ordinal) ||
                      effect.StartsWith("CopyPermissions", StringComparison.Ordinal) || effect.StartsWith("Journal", StringComparison.Ordinal));
        Assert.Empty(s.Journal.History);
    }

    // #735: "keep the original after clean" moves the backup into the originals folder instead of deleting it.
    private static readonly KeepOriginalOptions KeepInDefaultFolder = new([SwapScenario.Library], null);

    [Fact]
    public async Task With_the_setting_off_the_swap_is_unchanged()
    {
        var s = new SwapScenario();

        var result = await s.RunAsync(SwapOptions.Default);

        Assert.Null(result.KeptOriginalPath);
        Assert.True(result.BackupRemoved);
        Assert.Equal([Original], s.Files.Paths);
        // The same 23 operations as every other test in this file: nothing new runs while the setting is off.
        Assert.DoesNotContain(
            s.Files.Operations,
            op => op.StartsWith("EnsureDirectory", StringComparison.Ordinal) || op.StartsWith("TryReserve", StringComparison.Ordinal) ||
                  op.StartsWith("ReplaceReservation", StringComparison.Ordinal) || op.StartsWith("CopyOverReservation", StringComparison.Ordinal) ||
                  op.StartsWith("ContentHash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keeping_the_original_moves_it_into_the_originals_folder_instead_of_deleting_it()
    {
        var s = new SwapScenario();
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: KeepInDefaultFolder));

        Assert.Equal(destination, result.KeptOriginalPath);
        Assert.True(result.BackupRemoved);
        Assert.Empty(result.Warnings);
        Assert.Equal([destination, Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(destination));
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.False(s.Files.Has(Backup));
    }

    [Fact]
    public async Task A_name_already_at_the_destination_never_gets_overwritten()
    {
        var s = new SwapScenario();
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);
        s.Files.Put(destination, "an earlier kept original");

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: KeepInDefaultFolder));

        Assert.NotNull(result.KeptOriginalPath);
        Assert.NotEqual(destination, result.KeptOriginalPath);
        Assert.Equal("an earlier kept original", s.Files.Read(destination));
        Assert.Equal("OLD", s.Files.Read(result.KeptOriginalPath!));
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.False(s.Files.Has(Backup));
        Assert.Equal(3, s.Files.Paths.Count());
    }

    [Fact]
    public async Task A_cross_volume_keep_copies_then_deletes_the_backup_once_the_size_is_verified()
    {
        var s = new SwapScenario();
        s.Files.OtherVolumeFolder = "/originals-volume";
        var keep = new KeepOriginalOptions([SwapScenario.Library], "/originals-volume/kept");
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, "/originals-volume/kept", Original);

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: keep));

        Assert.Equal(destination, result.KeptOriginalPath);
        Assert.Equal("OLD", s.Files.Read(destination));
        Assert.False(s.Files.Has(Backup));
        Assert.Contains($"ReplaceReservation({Backup} -> {destination})", s.Files.Operations);
        Assert.Contains($"CopyOverReservation({Backup} -> {destination})", s.Files.Operations);
        Assert.Contains($"ContentHash({Backup})", s.Files.Operations);
        Assert.Contains($"ContentHash({destination})", s.Files.Operations);
    }

    [Fact]
    public async Task A_corrupt_cross_volume_copy_is_discarded_and_the_backup_is_left_for_the_sweep()
    {
        var s = new SwapScenario();
        s.Files.OtherVolumeFolder = "/originals-volume";
        s.Files.CorruptNextCopy = true;
        var keep = new KeepOriginalOptions([SwapScenario.Library], "/originals-volume/kept");
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, "/originals-volume/kept", Original);

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: keep));

        // The swap itself already committed (the library file holds the cleaned content); only the keep step failed.
        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.False(result.BackupRemoved);
        Assert.Single(result.Warnings);
        Assert.Null(result.KeptOriginalPath);
        Assert.False(s.Files.Has(destination), "a copy that failed its size check must not be left behind");
        Assert.Equal("OLD", s.Files.Read(Backup));
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_crash_at_the_move_into_the_originals_folder_is_finished_by_the_sweep_from_the_journal()
    {
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);
        var clean = new SwapScenario();
        await clean.RunAsync(new SwapOptions(KeepOriginal: KeepInDefaultFolder));
        var moveIntoPlaceIndex = clean.Files.Operations.IndexOf($"ReplaceReservation({Backup} -> {destination})");
        Assert.True(moveIntoPlaceIndex > 0);

        var s = new SwapScenario();
        s.Files.FaultAt = moveIntoPlaceIndex;
        s.Files.Mode = FaultMode.Crash;

        await s.RunAsync(new SwapOptions(KeepOriginal: KeepInDefaultFolder));
        Assert.True(s.Files.Crashed);
        // The crash landed before the move into the originals folder took effect: the backup is still where it was,
        // and the destination is still just the empty reservation the earlier "committing" step made.
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.True(s.Files.Has(Backup));
        Assert.True(s.Files.Has(destination));
        Assert.Equal(string.Empty, s.Files.Read(destination));

        s.Files.Restart();
        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(0, report.Problems);
        Assert.False(s.Files.Has(Backup));
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.Equal("OLD", s.Files.Read(destination));
        Assert.Equal([destination, Original], s.Files.Paths);

        // A further sweep (as if Weir restarted again) finds nothing left to do.
        var again = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);
        Assert.Equal(SwapRecoveryReport.Empty, again);
    }

    [Fact]
    public async Task A_crash_between_the_cross_volume_copy_and_deleting_the_backup_never_copies_twice()
    {
        var s = new SwapScenario();
        s.Files.OtherVolumeFolder = "/originals-volume";
        var keep = new KeepOriginalOptions([SwapScenario.Library], "/originals-volume/kept");
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, "/originals-volume/kept", Original);

        var clean = new SwapScenario();
        clean.Files.OtherVolumeFolder = "/originals-volume";
        await clean.RunAsync(new SwapOptions(KeepOriginal: keep));
        var deleteBackupIndex = clean.Files.Operations.IndexOf($"Delete({Backup})");
        Assert.True(deleteBackupIndex > 0);

        s.Files.FaultAt = deleteBackupIndex;
        s.Files.Mode = FaultMode.Crash;
        await s.RunAsync(new SwapOptions(KeepOriginal: keep));
        Assert.True(s.Files.Crashed);
        Assert.True(s.Files.Has(destination));
        Assert.True(s.Files.Has(Backup));

        s.Files.Restart();
        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(0, report.Problems);
        Assert.False(s.Files.Has(Backup));
        // The copy already happened before the crash; recovery only has to confirm it and finish the delete, not repeat it.
        Assert.Single(s.Files.Operations, op => op.StartsWith("CopyOverReservation(", StringComparison.Ordinal));
        Assert.Equal("OLD", s.Files.Read(destination));
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_rollback_after_the_reservation_cleans_up_the_empty_placeholder()
    {
        // "The original changed as Weir moved it aside" fires after the reservation is made but before the commit:
        // the reservation must not linger as a stray empty file once the swap gives up.
        var s = new SwapScenario();
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);
        s.Files.OnStep = operation =>
        {
            if (operation == $"Fingerprint({Backup})")
            {
                s.Files.Write(Backup, "OLD plus bytes a writer added through its open handle");
            }
        };

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: KeepInDefaultFolder));

        Assert.Equal(SwapOutcome.SourceChanged, result.Outcome);
        Assert.False(s.Files.Has(destination));
        Assert.Equal([Original], s.Files.Paths);
    }

    [Fact]
    public async Task A_crash_mid_copy_that_leaves_a_shorter_file_is_flagged_as_a_conflict_not_silently_finished()
    {
        // A crash while copying across volumes can leave a genuinely partial (shorter) file at the destination —
        // distinct from an untouched, still-empty reservation. Recovery must tell the two apart.
        var s = new SwapScenario();
        s.Files.OtherVolumeFolder = "/originals-volume";
        var keep = new KeepOriginalOptions([SwapScenario.Library], "/originals-volume/kept");
        var destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, "/originals-volume/kept", Original);
        s.Files.CorruptNextCopy = true;

        var result = await s.RunAsync(new SwapOptions(KeepOriginal: keep));
        Assert.False(result.BackupRemoved);
        Assert.False(s.Files.Has(destination), "the swap's own attempt discards a copy that fails verification");

        // Simulate the crash landing mid-copy instead: the partial bytes survive at the reserved name.
        s.Files.Put(destination, "OL");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(1, report.Problems);
        Assert.Equal([Original], report.KeepConflicts);
        Assert.True(s.Files.Has(Backup), "neither file is touched once a conflict is found");
        Assert.True(s.Files.Has(destination));
        Assert.Equal(SwapJournalState.KeepConflict, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.Single(s.ActivityWriter.Events);
        Assert.Contains("Weir kept both copies of", s.ActivityWriter.Events[0].Title, StringComparison.Ordinal);
        Assert.Equal(Original, ActivityClassifier.Classify(s.ActivityWriter.Events[0].EventType, s.ActivityWriter.Events[0].Detail).RelativePath);

        // The conflict is recorded once: a further sweep must not alert about it again or touch the files again.
        s.ActivityWriter.Events.Clear();
        var again = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);
        Assert.Equal(SwapRecoveryReport.Empty, again);
        Assert.Empty(s.ActivityWriter.Events);
        Assert.True(s.Files.Has(Backup));
        Assert.True(s.Files.Has(destination));
    }

    [Theory]
    [InlineData(7ul, 100ul, 3L, 5L, 7ul, 100ul, 3L, 5L, true)]
    [InlineData(7ul, 100ul, 3L, 5L, 7ul, 101ul, 3L, 5L, false)]
    [InlineData(7ul, 100ul, 3L, 5L, 7ul, 100ul, 4L, 5L, false)]
    [InlineData(7ul, 100ul, 3L, 5L, 7ul, 100ul, 3L, 6L, false)]
    [InlineData(0ul, 0ul, 3L, 5L, 7ul, 100ul, 3L, 5L, true)]
    [InlineData(7ul, 100ul, 3L, 5L, 0ul, 0ul, 3L, 5L, true)]
    public void Fingerprints_compare_identity_only_when_both_sides_read_it(
        ulong device1, ulong inode1, long size1, long mtime1, ulong device2, ulong inode2, long size2, long mtime2, bool same)
    {
        Assert.Equal(same, SafeSwap.SameFile(new SourceFingerprint(device1, inode1, size1, mtime1), new SourceFingerprint(device2, inode2, size2, mtime2)));
    }
}
