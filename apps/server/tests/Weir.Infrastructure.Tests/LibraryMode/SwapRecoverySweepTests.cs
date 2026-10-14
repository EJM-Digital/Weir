using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

public sealed class SwapRecoverySweepTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;

    [Fact]
    public async Task A_temp_file_is_deleted()
    {
        var s = new SwapScenario();
        s.Files.Put(Temp, "NEW");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 0), report);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_backup_beside_its_original_is_deleted()
    {
        var s = new SwapScenario();
        s.Files.Write(Original, "NEW");
        s.Files.Put(Backup, "OLD");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), report);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    [Fact]
    public async Task A_backup_whose_original_is_missing_is_restored_with_a_warning()
    {
        var s = new SwapScenario();
        s.Files.Delete(Original);
        s.Files.Put(Backup, "OLD");
        s.Files.Put(Temp, "NEW");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(1, 0, 1, 0), report);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Contains(
            s.SweepLogger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Weir restored a library file from its backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recorded_swaps_are_recovered_without_walking_any_folder()
    {
        var s = new SwapScenario();
        const string elsewhere = "/other-library/Show/S01E01.mkv";
        s.Files.Put(SafeSwapRules.BackupPath(elsewhere), "OLD");
        await s.Journal.RecordAsync(new SwapJournalEntry(7, elsewhere, SwapJournalState.Committing));

        var report = await s.Sweep.RunAsync([], walkFolders: false);

        Assert.Equal(new SwapRecoveryReport(0, 0, 1, 0), report);
        Assert.Equal("OLD", s.Files.Read(elsewhere));
        Assert.Equal(SwapJournalState.Recovered, s.Journal.Latest(7)!.State);
        Assert.DoesNotContain(s.Files.Operations, operation => operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_walk_unrecorded_leftovers_are_left_alone()
    {
        var s = new SwapScenario();
        s.Files.Put(Temp, "NEW");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(SwapRecoveryReport.Empty, report);
        Assert.True(s.Files.Has(Temp));
    }

    [Fact]
    public async Task A_file_recorded_and_found_by_the_walk_is_handled_once()
    {
        var s = new SwapScenario();
        s.Files.Put(Temp, "NEW");
        await s.Journal.RecordAsync(new SwapJournalEntry(SwapScenario.JobId, Original, SwapJournalState.Writing));

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 0), report);
        Assert.Single(s.Files.Operations, operation => operation == $"FileExists({Backup})");
    }

    [Fact]
    public async Task Finished_swaps_on_the_journal_are_not_revisited()
    {
        var s = new SwapScenario();
        await s.Journal.RecordAsync(new SwapJournalEntry(1, Original, SwapJournalState.Finished));
        await s.Journal.RecordAsync(new SwapJournalEntry(2, "/lib/b.mkv", SwapJournalState.RolledBack));
        s.Files.Operations.Clear();

        await s.Sweep.RunAsync([], walkFolders: false);

        Assert.Equal(["Journal(list)", "Journal(conflicts)"], s.Files.Operations);
    }

    [Fact]
    public async Task A_journal_that_cannot_be_read_still_lets_the_walk_recover()
    {
        var s = new SwapScenario();
        s.Files.Delete(Original);
        s.Files.Put(Backup, "OLD");
        s.Files.OnStep = operation =>
        {
            if (operation == "Journal(list)")
            {
                s.Files.FaultAt = s.Files.Operations.Count - 1;
            }
        };

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(0, 0, 1, 1), report);
        Assert.Equal("OLD", s.Files.Read(Original));
    }

    [Fact]
    public async Task Names_that_only_look_like_leftovers_are_never_touched()
    {
        var s = new SwapScenario();
        string[] decoys =
        [
            "/lib/Movie (2020)/Movie (2020).weir-tmp-notes.txt",
            "/lib/Movie (2020)/Movie (2020).WEIR-TMP.mkv",
            "/lib/Movie (2020)/Movie (2020).weir-bak.mkv.part",
            "/lib/Movie (2020)/weir-tmp.mkv",
        ];
        foreach (var decoy in decoys)
        {
            s.Files.Put(decoy, "keep me");
        }

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(SwapRecoveryReport.Empty, report);
        Assert.All(decoys, decoy => Assert.True(s.Files.Has(decoy)));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_walked_is_counted_and_the_others_still_are()
    {
        var s = new SwapScenario();
        s.Files.Put("/lib2/a.weir-tmp.mkv", "NEW");
        s.Files.OnStep = operation =>
        {
            if (operation == "EnumerateLeftovers(/lib)")
            {
                s.Files.FaultAt = s.Files.Operations.Count - 1;
            }
        };

        var report = await s.Sweep.RunAsync(["/lib", "/lib2"], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 1), report);
        Assert.False(s.Files.Has("/lib2/a.weir-tmp.mkv"));
    }

    [Fact]
    public async Task A_leftover_that_cannot_be_removed_is_counted_and_stays_unfinished_on_the_journal()
    {
        var s = new SwapScenario();
        s.Files.Put(Backup, "OLD");
        s.Files.LockedForRename.Add(Backup);
        await s.Journal.RecordAsync(new SwapJournalEntry(SwapScenario.JobId, Original, SwapJournalState.Committed));

        var report = await s.Sweep.RunAsync([], walkFolders: false);

        Assert.Equal(1, report.Problems);
        Assert.Equal(SwapJournalState.Committed, s.Journal.Latest(SwapScenario.JobId)!.State);
    }
}
