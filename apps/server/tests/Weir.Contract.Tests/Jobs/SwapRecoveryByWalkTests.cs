using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// The swaps the database has a record of are put right first, and the library folders are walked for anything it does not know
/// about: a record that could not be written, or a job row already pruned. Here the record is removed after the crash, so only the
/// files can say a swap was started.
/// </summary>
[ContractArea("jobs")]
public sealed class SwapRecoveryByWalkTests
{
    [Theory]
    [InlineData("CopyChecked")]
    [InlineData("OriginalMovedAside")]
    [InlineData("CleanedCopyInPlace")]
    public async Task Leftovers_with_no_record_in_the_database_are_found_by_walking_the_library_folders(string point)
    {
        await using var world = await SwapWorld.StartAsync(keepOriginal: false, holdAt: point);
        await world.RequestCleanAsync();
        await world.HeldAtAsync();
        await world.CrashAsync();
        var filmWhenStopped = File.Exists(world.FilePath) ? await File.ReadAllBytesAsync(world.FilePath) : null;
        await world.ForgetTheSwapsAsync();

        await world.RestartAsync(workers: 0);

        Assert.Equal(SwapCrashPlan.FilesAfterRecovery(point, keepOriginal: false), world.FilesInLibrary());
        var film = await File.ReadAllBytesAsync(world.FilePath);
        if (SwapCrashPlan.Commits(point))
        {
            Assert.Equal(filmWhenStopped, film);
        }
        else
        {
            Assert.Equal(SwapWorld.OriginalBytes, film);
        }

        Assert.Empty(await world.SwapStatesAsync());
    }
}
