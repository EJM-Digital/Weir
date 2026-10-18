using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>What a recovery reports, logs and counts for each way a swap can have been left half done.</summary>
public sealed class SwapRecoveryDetailTests
{
    private const string Original = SwapScenario.Original;
    private const string Kept = "/originals/Movie (2020).mkv";
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;

    private sealed class FailingActivityWriter(Exception failure) : IActivityWriter
    {
        public Task<long> RecordAsync(ActivityEventDraft draft, CancellationToken cancellationToken = default) => Task.FromException<long>(failure);
    }

    private static bool Logged(ListLogger<SwapRecoverySweep> logger, LogLevel level, string fragment) =>
        logger.Entries.Any(entry => entry.Level == level && entry.Message.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void The_files_a_recovery_changed_are_the_temp_copies_deleted_plus_the_backups_deleted_plus_the_backups_restored()
    {
        Assert.Equal(7, new SwapRecoveryReport(1, 2, 4, 8).FilesChanged);
        Assert.Equal(0, SwapRecoveryReport.Empty.FilesChanged);
    }

    [Fact]
    public void Two_reports_are_equal_only_when_every_count_and_every_conflict_matches()
    {
        var report = new SwapRecoveryReport(1, 2, 3, 4, ["/a"]);

        Assert.True(report.Equals(new SwapRecoveryReport(1, 2, 3, 4, ["/a"])));
        Assert.False(report.Equals(null));
        Assert.False(report.Equals(new SwapRecoveryReport(9, 2, 3, 4, ["/a"])));
        Assert.False(report.Equals(new SwapRecoveryReport(1, 9, 3, 4, ["/a"])));
        Assert.False(report.Equals(new SwapRecoveryReport(1, 2, 9, 4, ["/a"])));
        Assert.False(report.Equals(new SwapRecoveryReport(1, 2, 3, 9, ["/a"])));
        Assert.False(report.Equals(new SwapRecoveryReport(1, 2, 3, 4, ["/b"])));
        Assert.False(report.Equals(new SwapRecoveryReport(1, 2, 3, 4)));
        Assert.Equal(new SwapRecoveryReport(1, 2, 3, 4), new SwapRecoveryReport(1, 2, 3, 4, []));
        Assert.Equal(report.GetHashCode(), new SwapRecoveryReport(1, 2, 3, 4, ["/a"]).GetHashCode());
    }

    [Fact]
    public void Adding_reports_adds_every_count_and_joins_the_conflicts_in_order()
    {
        var left = new SwapRecoveryReport(1, 2, 4, 8, ["/left"]);
        var right = new SwapRecoveryReport(16, 32, 64, 128, ["/right"]);

        var sum = left + right;

        Assert.Equal(new SwapRecoveryReport(17, 34, 68, 136, ["/left", "/right"]), sum);
        Assert.Equal(sum, SwapRecoveryReport.Add(left, right));
    }

    [Fact]
    public void A_backup_beside_its_original_is_deleted_and_the_log_says_the_original_name_kept_its_file()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "NEW");
        files.Put(Backup, "OLD");
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), report);
        Assert.Equal([Original], files.Paths);
        Assert.True(Logged(logger, LogLevel.Information, "backup removed; the file under the original name is kept"));
    }

    [Fact]
    public void A_backup_beside_its_original_is_moved_to_the_kept_place_when_the_journal_names_one()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "NEW");
        files.Put(Backup, "OLD");
        OriginalsMover.Reserve(files, Kept);
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger, keptOriginalPath: Kept);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), report);
        Assert.Equal("OLD", files.Read(Kept));
        Assert.Equal([Original, Kept], files.Paths);
        Assert.True(Logged(logger, LogLevel.Information, "original moved to its kept location"));
        Assert.False(Logged(logger, LogLevel.Information, "backup removed"));
    }

    [Fact]
    public void A_backup_with_no_original_is_put_back_with_a_warning_and_its_unused_kept_place_is_released()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Backup, "OLD");
        OriginalsMover.Reserve(files, Kept);
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger, keptOriginalPath: Kept);

        Assert.Equal(new SwapRecoveryReport(0, 0, 1, 0), report);
        Assert.Equal([Original], files.Paths);
        Assert.Equal("OLD", files.Read(Original));
        Assert.True(Logged(logger, LogLevel.Warning, "restored a library file from its backup"));
    }

    [Fact]
    public void A_rollback_putting_its_own_backup_back_is_logged_as_information_not_a_warning()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Backup, "OLD");
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger, restoreQuietly: true);

        Assert.Equal(new SwapRecoveryReport(0, 0, 1, 0), report);
        Assert.True(Logged(logger, LogLevel.Information, "put the original back"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_cleaned_copy_that_was_never_used_releases_the_kept_place_that_was_reserved_for_it()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "OLD");
        files.Put(Temp, "NEW");
        OriginalsMover.Reserve(files, Kept);

        var report = SwapRecoverySweep.RecoverFile(files, Original, new ListLogger<SwapRecoverySweep>(), keptOriginalPath: Kept);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 0), report);
        Assert.Equal([Original], files.Paths);
    }

    [Fact]
    public void A_kept_place_that_already_holds_a_file_is_never_released_by_a_recovery()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "OLD");
        files.Put(Temp, "NEW");
        files.Put(Kept, "an earlier original");

        SwapRecoverySweep.RecoverFile(files, Original, new ListLogger<SwapRecoverySweep>(), keptOriginalPath: Kept);

        Assert.Equal("an earlier original", files.Read(Kept));
    }

    [Fact]
    public void A_backup_that_cannot_be_deleted_is_counted_as_a_problem_and_logged_and_the_temp_copy_is_still_removed()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "NEW");
        files.Put(Backup, "OLD");
        files.Put(Temp, "NEW");
        files.LockedForRename.Add(Backup);
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 1), report);
        Assert.True(files.Has(Backup));
        Assert.True(Logged(logger, LogLevel.Warning, "could not deal with the backup"));
    }

    [Fact]
    public void A_temp_copy_that_cannot_be_deleted_is_counted_as_a_problem_and_logged()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Original, "OLD");
        files.Put(Temp, "NEW");
        files.LockedForRename.Add(Temp);
        var logger = new ListLogger<SwapRecoverySweep>();

        var report = SwapRecoverySweep.RecoverFile(files, Original, logger);

        Assert.Equal(new SwapRecoveryReport(0, 0, 0, 1), report);
        Assert.True(Logged(logger, LogLevel.Warning, "could not delete the temp file"));
    }

    [Fact]
    public void A_cancellation_during_a_recovery_is_not_counted_as_a_problem()
    {
        var files = new FakeSwapFileSystem();
        files.Put(Backup, "OLD");
        files.FaultAt = 0;
        files.FaultException = () => new OperationCanceledException();
        var logger = new ListLogger<SwapRecoverySweep>();

        Assert.Throws<OperationCanceledException>(() => SwapRecoverySweep.RecoverFile(files, Original, logger));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task A_sweep_that_changed_files_or_met_a_problem_logs_one_warning_with_the_counts_and_a_quiet_one_logs_nothing()
    {
        var changed = new SwapScenario();
        changed.Files.Put(Temp, "NEW");
        await changed.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);
        Assert.True(Logged(changed.SweepLogger, LogLevel.Warning, "temp_files_deleted=1 backups_deleted=0 backups_restored=0 problems=0"));

        var troubled = new SwapScenario();
        troubled.Files.Put(Backup, "OLD");
        troubled.Files.LockedForRename.Add(Backup);
        await troubled.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);
        Assert.True(Logged(troubled.SweepLogger, LogLevel.Warning, "temp_files_deleted=0 backups_deleted=0 backups_restored=0 problems=1"));

        var quiet = new SwapScenario();
        await quiet.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);
        Assert.DoesNotContain(quiet.SweepLogger.Entries, entry => entry.Message.Contains("sweep finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_kept_original_conflict_is_told_to_the_person_once_with_the_file_and_both_paths()
    {
        var s = new SwapScenario();
        await RecordConflictAsync(s);

        var draft = Assert.Single(s.ActivityWriter.Events);

        Assert.Equal(LibraryActivityEventTypes.OriginalKeepConflict, draft.EventType);
        Assert.Equal("library", draft.Module);
        Assert.Equal($"Weir kept both copies of Movie (2020).mkv; check {Kept} and remove the incomplete one.", draft.Title);
        Assert.Contains($"\"relative_media_path\":\"{Original}\"", draft.Detail, StringComparison.Ordinal);
        Assert.Contains($"\"kept_original_path\":\"{Kept}\"", draft.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflict_whose_activity_event_cannot_be_written_is_still_marked_on_the_journal_and_logged()
    {
        var s = new SwapScenario();
        var sweep = new SwapRecoverySweep(s.Files, s.Journal, new FailingActivityWriter(new IOException("activity is down")), s.SweepLogger);

        await RecordConflictAsync(s, sweep);

        Assert.Equal(SwapJournalState.KeepConflict, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.True(Logged(s.SweepLogger, LogLevel.Warning, "found a kept-original conflict but could not record it"));
    }

    [Fact]
    public async Task A_conflict_that_cannot_be_marked_on_the_journal_is_logged_and_the_sweep_carries_on()
    {
        var s = new SwapScenario();
        await RecordConflictAsync(s, mark: () => s.Files.OnStep = operation =>
        {
            if (operation == "Journal(KeepConflict)")
            {
                throw new IOException("journal is read-only");
            }
        });

        Assert.Equal(SwapJournalState.Committing, s.Journal.Latest(SwapScenario.JobId)!.State);
        Assert.True(Logged(s.SweepLogger, LogLevel.Warning, "could not mark a kept-original conflict on the journal"));
    }

    [Fact]
    public async Task A_cancelled_activity_write_for_a_conflict_is_not_swallowed()
    {
        var s = new SwapScenario();
        var sweep = new SwapRecoverySweep(s.Files, s.Journal, new FailingActivityWriter(new OperationCanceledException()), s.SweepLogger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => RecordConflictAsync(s, sweep));
    }

    /// <summary>A swap that crashed while moving its original into the originals folder, leaving a kept file that does not match its backup.</summary>
    private static async Task RecordConflictAsync(SwapScenario s, SwapRecoverySweep? sweep = null, Action? mark = null)
    {
        s.Files.Put(Backup, "OLD");
        s.Files.Put(Original, "NEW");
        s.Files.Put(Kept, "OL");
        await s.Journal.RecordAsync(new SwapJournalEntry(SwapScenario.JobId, Original, SwapJournalState.Committing, Kept));
        mark?.Invoke();

        var report = await (sweep ?? s.Sweep).RunAsync([], walkFolders: false);

        Assert.Equal([Original], report.KeepConflicts);
    }
}
