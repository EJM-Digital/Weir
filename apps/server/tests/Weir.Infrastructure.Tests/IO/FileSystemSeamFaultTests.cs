using Weir.Core.MediaManagers;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.IO;

/// <summary>
/// One example for each family of file handling that goes through <see cref="Weir.Infrastructure.IO.IFileSystem"/>: the
/// operating system's failure is injected at the one call, and the code is shown to behave as it does when the real disk fails.
/// </summary>
public sealed class FileSystemSeamFaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string relative, int size = 8)
    {
        var path = _dir.Join(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void A_fingerprint_of_a_file_that_vanishes_before_it_is_measured_says_the_file_is_not_there()
    {
        var film = Write("Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished);

        Assert.Throws<FileNotFoundException>(() => SourceFiles.Fingerprint(film, files));
        Assert.Equal("unreadable", SourceFiles.DedupeFingerprintTag(film, new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished)));
    }

    [Fact]
    public void A_new_output_that_cannot_be_measured_leaves_the_existing_one_in_place()
    {
        var final = Write("out/Film.mkv", size: 10);
        var staged = Write("work/Film.mkv", size: 20);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation, pathEndsWith: Path.Join("work", "Film.mkv"));

        var decision = OutputCollision.Decide(final, staged: staged, policy: OutputCollision.ReplaceIfLarger, files: files);

        Assert.Equal("skip", decision.Action);
        Assert.Contains("could not compare", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_folder_that_cannot_be_read_costs_only_the_file_not_the_folder()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Film (2001)/Film.mkv");
        var other = Write("movies/Film (2001)/Extras/Other.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFiles, FaultingFileSystem.Refused);

        var removal = ReleaseFolderRemoval.Remove(root, film, null, files);

        Assert.Equal(ReleaseRemovalKind.FileOnly, removal.Kind);
        Assert.Equal(ReleaseFolderRemoval.UnreadableFolderReason, removal.Reason);
        Assert.False(File.Exists(film));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_watched_subfolder_that_vanishes_under_the_walk_costs_only_itself()
    {
        var watched = _dir.Join("watch");
        Write("watch/a/A.mkv");
        Write("watch/b/B.mkv");
        Write("watch/c/C.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFileSystemInfos, FaultingFileSystem.FolderVanished, pathEndsWith: "b");

        var found = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false, files);

        Assert.Equal(["A.mkv", "C.mkv"], found.Files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_copy_whose_time_cannot_be_read_is_not_measured_for_the_hand_back()
    {
        var copy = Write("out/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused);

        Assert.False(HandbackStore.TryMeasure(copy, out _, out _, files));
    }

    [Fact]
    public void A_folder_hand_off_whose_folder_is_refused_is_treated_as_the_path_itself()
    {
        Write("watch/Film (2001)/Film.mkv");
        var library = new IntakeLibrary(1, "movie", _dir.Join("watch"));
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFiles, FaultingFileSystem.Refused);

        Assert.Equal(["Film (2001)"], MediaManagerIntake.HandoffMediaFiles(library, "Film (2001)", files));
    }

    [Fact]
    public void A_rejected_file_that_is_locked_when_it_is_deleted_stays_and_says_why()
    {
        var watched = _dir.Join("watch");
        var rejected = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, rejected, "delete_file", files);

        Assert.False(cleanup.Deleted);
        Assert.Contains("locked or unavailable", cleanup.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(rejected));
    }

    [Fact]
    public void A_write_probe_the_folder_refuses_fails_the_swap_check_with_the_system_error()
    {
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused));

        Assert.Throws<UnauthorizedAccessException>(() => swap.ProbeWrite(_dir.Path));
    }

    [Fact]
    public void A_work_file_that_cannot_be_deleted_is_left_for_the_next_look()
    {
        var work = Write("work/Film.partial");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);

        FileLifecycle.BestEffortDelete(work, files);

        Assert.True(File.Exists(work));
    }
}
