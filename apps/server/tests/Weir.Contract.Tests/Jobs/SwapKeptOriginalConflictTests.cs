using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// A kept original the recovery finds already at its destination, with content that is not the backup's, is a crash in the middle
/// of a copy. Weir does not guess: it says so once in Activity and leaves both files for a person (docs/file-lifecycle-contract.md).
/// </summary>
[ContractArea("jobs")]
public sealed class SwapKeptOriginalConflictTests
{
    private const string ConflictEvent = "library.original_keep_conflict";

    [Fact]
    public async Task A_kept_original_that_does_not_match_its_backup_is_reported_once_and_both_files_are_left()
    {
        await using var world = await SwapWorld.StartAsync(keepOriginal: true, holdAt: "CleanedCopyInPlace");
        await world.RequestCleanAsync();
        await world.HeldAtAsync();
        await world.CrashAsync();
        var cleaned = await File.ReadAllBytesAsync(world.FilePath);
        byte[] halfCopied = [1, 2, 3];
        await File.WriteAllBytesAsync(world.KeptPath, halfCopied);

        await world.RestartAsync(workers: 0);

        var conflict = Assert.Single(await world.ActivityOfTypeAsync(ConflictEvent));
        Assert.Equal(
            $"Weir kept both copies of Film (2020).mkv; check {world.KeptPath} and remove the incomplete one.",
            (string)conflict["title"]!);
        Assert.Equal(halfCopied, await File.ReadAllBytesAsync(world.KeptPath));
        Assert.Equal(SwapWorld.OriginalBytes, await File.ReadAllBytesAsync(world.BackupPath));
        Assert.Equal(cleaned, await File.ReadAllBytesAsync(world.FilePath));
        Assert.Equal(["keep_conflict"], await world.SwapStatesAsync());

        // Recorded once and then left: a later start finds nothing unfinished, so it says nothing more and changes nothing.
        await world.RestartAsync(workers: 0);

        Assert.Single(await world.ActivityOfTypeAsync(ConflictEvent));
        Assert.Equal(halfCopied, await File.ReadAllBytesAsync(world.KeptPath));
        Assert.Equal(SwapWorld.OriginalBytes, await File.ReadAllBytesAsync(world.BackupPath));
    }
}
