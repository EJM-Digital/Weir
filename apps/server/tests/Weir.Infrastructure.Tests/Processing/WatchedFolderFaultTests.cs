using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>A watched folder or file that another program holds, refuses or removes while Weir reads it.</summary>
public sealed class WatchedFolderFaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string relative)
    {
        var path = _dir.Join(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[8]);
        return path;
    }

    [Fact]
    public void A_file_that_vanishes_while_it_is_measured_is_not_there()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished);

        Assert.Null(WatchedMediaFile.Stat(film, files));
        Assert.NotNull(WatchedMediaFile.Stat(film));
    }

    [Fact]
    public void A_file_the_system_refuses_to_date_is_not_there()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused);

        Assert.Null(WatchedMediaFile.Stat(film, files));
    }

    [Fact]
    public void A_folder_Weir_may_not_read_fails_the_listing_instead_of_hiding_the_files_in_it()
    {
        var watched = _dir.Join("watch");
        Write("watch/a/A.mkv");
        Write("watch/b/B.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFileSystemInfos, FaultingFileSystem.Refused, pathEndsWith: "b");

        Assert.Throws<UnauthorizedAccessException>(() => WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false, files));
    }

    [Fact]
    public void A_watched_folder_removed_under_the_walk_lists_nothing_and_does_not_fail()
    {
        var watched = _dir.Join("watch");
        Write("watch/A.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFileSystemInfos, FaultingFileSystem.FolderVanished);

        var found = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false, files);

        Assert.Empty(found.Entries);
        Assert.Equal(0, found.IgnoredUnsupportedType);
    }

    [Fact]
    public void A_source_that_is_locked_for_reading_says_so_in_plain_words()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.SharingViolation);

        var problem = WatchedFolderScanOps.SourceReadProblem(film, files);

        Assert.StartsWith("Weir could not open this file for reading", problem, StringComparison.Ordinal);
        Assert.Contains("still writing it", problem, StringComparison.Ordinal);
        Assert.Null(WatchedFolderScanOps.SourceReadProblem(film));
    }
}
