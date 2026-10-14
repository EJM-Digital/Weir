using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// The place a kept original will go is reserved before the commit. A crash after that and before the original was moved aside
/// leaves the original in place, so the reservation is empty and must be released with the cleaned copy that was never used.
/// </summary>
public sealed class SwapRecoveryReservationTests
{
    private const string Original = SwapScenario.Original;
    private static readonly string Temp = SwapScenario.Temp;
    private static readonly string Backup = SwapScenario.Backup;
    private static readonly KeepOriginalOptions Keep = new([SwapScenario.Library], null);
    private static readonly string Reservation = OriginalsPathPlanner.DestinationPath(SwapScenario.Library, null, Original);

    [Fact]
    public async Task A_crash_after_the_reservation_and_before_the_original_moved_aside_releases_the_reservation()
    {
        var s = await CrashedBeforeTheOriginalMovedAsideAsync();

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 0), report);
        Assert.Equal([Original], s.Files.Paths);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal(SwapJournalState.Recovered, s.Journal.Latest(SwapScenario.JobId)!.State);
    }

    [Fact]
    public async Task A_reservation_with_a_kept_original_in_it_is_never_released()
    {
        var s = await CrashedBeforeTheOriginalMovedAsideAsync();
        s.Files.Write(Reservation, "OLD");

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(new SwapRecoveryReport(1, 0, 0, 0), report);
        Assert.Equal("OLD", s.Files.Read(Reservation));
        Assert.True(s.Files.Has(Original));
    }

    [Fact]
    public async Task A_reservation_beside_a_finished_swap_is_left_alone()
    {
        var s = new SwapScenario();
        await s.RunAsync(new SwapOptions(KeepOriginal: Keep));
        Assert.Equal("OLD", s.Files.Read(Reservation));

        var report = await s.Sweep.RunAsync([SwapScenario.Library], walkFolders: false);

        Assert.Equal(SwapRecoveryReport.Empty, report);
        Assert.Equal("OLD", s.Files.Read(Reservation));
        Assert.Equal("NEW", s.Files.Read(Original));
    }

    private static async Task<SwapScenario> CrashedBeforeTheOriginalMovedAsideAsync()
    {
        var clean = new SwapScenario();
        await clean.RunAsync(new SwapOptions(KeepOriginal: Keep));
        var movesOriginalAside = clean.Files.Operations.IndexOf($"Move({Original} -> {Backup})");
        Assert.True(movesOriginalAside > 0);

        var s = new SwapScenario();
        s.Files.FaultAt = movesOriginalAside;
        s.Files.Mode = FaultMode.Crash;
        await s.RunAsync(new SwapOptions(KeepOriginal: Keep));
        Assert.True(s.Files.Crashed);
        Assert.Equal("OLD", s.Files.Read(Original));
        Assert.Equal("NEW", s.Files.Read(Temp));
        Assert.False(s.Files.Has(Backup));
        Assert.Equal(string.Empty, s.Files.Read(Reservation));
        s.Files.Restart();
        return s;
    }
}
