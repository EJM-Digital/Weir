namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// One crash of the real server in the middle of a library clean, and what Weir does about it at the next start. The server is
/// ended at <c>point</c> of the swap with the job still running, so the files on disk are what a power cut there leaves; it is
/// then started again with no workers, so only start-up recovery has touched anything when the files are looked at.
/// </summary>
internal static class SwapCrashRecoveryChecks
{
    public static async Task RunAsync(string point, bool keepOriginal)
    {
        await using var world = await SwapWorld.StartAsync(keepOriginal, holdAt: point);
        await world.RequestCleanAsync();
        Assert.Equal(point, await world.HeldAtAsync());
        await world.CrashAsync();

        Assert.Equal(SwapCrashPlan.FilesWhenStopped(point, keepOriginal), world.FilesInLibrary());
        var filmWhenStopped = File.Exists(world.FilePath) ? await File.ReadAllBytesAsync(world.FilePath) : null;
        if (keepOriginal && point is "CommitRecorded" or "OriginalMovedAside")
        {
            Assert.Equal(0, new FileInfo(world.KeptPath).Length);
        }

        await world.RestartAsync(workers: 0);

        Assert.Equal(SwapCrashPlan.FilesAfterRecovery(point, keepOriginal), world.FilesInLibrary());
        var film = await File.ReadAllBytesAsync(world.FilePath);
        if (SwapCrashPlan.Commits(point))
        {
            Assert.Equal(filmWhenStopped, film);
            Assert.NotEqual(SwapWorld.OriginalBytes, film);
            Assert.Equal(keepOriginal, await world.KeptOriginalsAreUncleanedAsync());
        }
        else
        {
            Assert.Equal(SwapWorld.OriginalBytes, film);
        }

        // The job the crash interrupted is not lost: it is waiting to run again.
        Assert.Equal("pending", (string?)(await world.CleanJobAsync())["status"]);
        Assert.Equal([SwapCrashPlan.RecordAfterRecovery(point)], await world.SwapStatesAsync());
    }
}
