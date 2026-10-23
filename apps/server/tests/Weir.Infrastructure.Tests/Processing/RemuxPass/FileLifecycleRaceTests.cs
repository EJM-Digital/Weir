using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Copying and deleting files while something else, such as a progress display or another program, gets in the way.</summary>
public sealed class FileLifecycleRaceTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task A_progress_display_that_fails_does_not_spoil_the_copy()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var final = _root.Join("out", "final.mkv");
        var reports = 0;

        await FileLifecycle.SafeCopyToFinalAsync(
            source,
            final,
            progressCallback: (_, _) =>
            {
                reports++;
                throw new InvalidOperationException("the display is gone");
            });

        Assert.True(reports > 0);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(final));
        Assert.Equal(["final.mkv"], Directory.EnumerateFileSystemEntries(_root.Join("out")).Select(Path.GetFileName));
    }

    [WindowsFact("Another program holding a file open blocks its removal on Windows only.")]
    public void A_file_another_program_holds_is_left_in_place_when_a_best_effort_delete_cannot_remove_it()
    {
        var path = _root.Join("held.tmp");
        File.WriteAllText(path, "held");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FileLifecycle.BestEffortDelete(path);
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_file_that_is_already_gone_is_not_a_problem_for_a_best_effort_delete()
    {
        var path = _root.Join("never-there.tmp");

        FileLifecycle.BestEffortDelete(path);

        Assert.False(File.Exists(path));
    }
}
