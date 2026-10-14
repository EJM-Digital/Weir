using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>A kept original that does not match its backup is left for a person, and the start-up walk of the library folders must not undo that.</summary>
public sealed class SwapRecoveryConflictTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Backup = SwapScenario.Backup;
    private static readonly KeepOriginalOptions Keep = new([SwapScenario.Library], "/originals-volume/kept");
    private static readonly string Destination = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, "/originals-volume/kept", Original);

    [Fact]
    public async Task A_later_sweep_that_walks_the_library_folders_leaves_a_recorded_conflict_alone()
    {
        var s = await ConflictRecordedAsync();

        var again = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(SwapRecoveryReport.Empty, again);
        Assert.Equal("OLD", s.Files.Read(Backup));
        Assert.Equal("OL", s.Files.Read(Destination));
        Assert.Equal("NEW", s.Files.Read(Original));
        Assert.Single(s.ActivityWriter.Events);
    }

    [Fact]
    public async Task A_backup_beside_a_recorded_conflict_is_not_taken_for_one_the_journal_knows_nothing_about()
    {
        var s = await ConflictRecordedAsync();
        s.Files.Put(SafeSwapRules.BackupPath("/lib/Other (2021)/Other (2021).mkv"), "OLD");
        s.Files.Put("/lib/Other (2021)/Other (2021).mkv", "NEW");

        var again = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(0, 1, 0, 0), again);
        Assert.True(s.Files.Has(Backup));
        Assert.False(s.Files.Has(SafeSwapRules.BackupPath("/lib/Other (2021)/Other (2021).mkv")));
    }

    [Fact]
    public async Task When_the_conflicts_cannot_be_read_no_folder_is_walked_and_the_problem_is_counted()
    {
        var s = new SwapScenario();
        s.Files.Put(Backup, "OLD");
        s.Files.FaultAt = 1;
        s.Files.Mode = FaultMode.Throw;

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal(new SwapRecoveryReport(0, 0, 0, 1), report);
        Assert.True(s.Files.Has(Backup));
        Assert.DoesNotContain(s.Files.Operations, operation => operation.StartsWith("EnumerateLeftovers", StringComparison.Ordinal));
    }

    private static async Task<SwapScenario> ConflictRecordedAsync()
    {
        var s = new SwapScenario();
        s.Files.OtherVolumeFolder = "/originals-volume";
        s.Files.CorruptNextCopy = true;
        await s.RunAsync(new SwapOptions(KeepOriginal: Keep));
        s.Files.Put(Destination, "OL");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: true);

        Assert.Equal([Original], report.KeepConflicts);
        Assert.Equal(SwapJournalState.KeepConflict, s.Journal.Latest(SwapScenario.JobId)!.State);
        return s;
    }
}
