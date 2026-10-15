using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// No job is claimed before the start-up sweep has put the library right (FL-20, P0). The clean the crash interrupted is the job
/// that would be hurt by a claim that came too soon: it reads the film first, and the film is not under its name until the sweep
/// has renamed the backup back.
/// </summary>
[ContractArea("jobs")]
public sealed class SwapCrashRerunTests
{
    [Fact]
    public async Task The_clean_a_crash_interrupted_runs_again_on_the_original_the_sweep_put_back()
    {
        await using var world = await SwapWorld.StartAsync(keepOriginal: true, holdAt: "OriginalMovedAside");
        await world.RequestCleanAsync();
        await world.HeldAtAsync();
        await world.CrashAsync();
        Assert.False(File.Exists(world.FilePath), "the crash must leave nothing under the film's name for a job that came too soon to find");

        await world.RestartAsync(workers: 1);
        await world.WaitWhileWorkedOnAsync(
            async () => (string?)(await world.CleanJobAsync())["status"] == "completed",
            "the interrupted clean to run again and finish");

        var job = await world.CleanJobAsync();
        Assert.Equal(2, (int)job["attempt_count"]!);
        Assert.Null((string?)job["last_error"]);
        Assert.Empty(await world.FailedCleansAsync());
        Assert.Equal(SwapCrashPlan.FilesAfterRecovery("CleanedCopyInPlace", keepOriginal: true), world.FilesInLibrary());
        Assert.NotEqual(SwapWorld.OriginalBytes, await File.ReadAllBytesAsync(world.FilePath));
        Assert.True(await world.KeptOriginalsAreUncleanedAsync(), "the kept original is the film as it was before the clean");
        Assert.Equal(["finished"], await world.SwapStatesAsync());
    }
}
