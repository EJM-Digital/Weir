using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Which library folders the walk looks in, and what it does with a file that is listed but not there.</summary>
public sealed class LibraryFileWalkerFoldersTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ProcessingLibraryRecord Library() => new() { Name = "Movies", MediaExtensionsCsv = "mkv" };

    private static LibrarySettings Folders(params string[] folders) => new(folders, ScheduleEnabled: false, KeepOriginalAfterClean: false);

    private string Write(string folder, params string[] nameParts)
    {
        var path = _dir.Join([folder, .. nameParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void A_blank_or_missing_folder_is_skipped_and_the_others_are_walked()
    {
        var film = Write("films", "Film.mkv");

        var walked = LibraryFileWalker.Walk(Library(), Folders(string.Empty, "  ", _dir.Join("missing"), _dir.Join("films")));

        Assert.Equal([film], walked.Select(file => file.Path));
    }

    [Fact]
    public void A_file_reached_through_two_library_folders_is_listed_once()
    {
        var film = Write("films", "Nested", "Film.mkv");

        var walked = LibraryFileWalker.Walk(Library(), Folders(_dir.Join("films"), _dir.Join("films"), _dir.Join("films", "Nested")));

        Assert.Equal([film], walked.Select(file => file.Path));
    }

    [Fact]
    public void A_file_is_listed_with_its_size_and_modified_time()
    {
        var film = Write("films", "Film.mkv");
        File.SetLastWriteTimeUtc(film, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        var file = Assert.Single(LibraryFileWalker.Walk(Library(), Folders(_dir.Join("films"))));

        Assert.Equal(3, file.SizeBytes);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero).ToUnixTimeSeconds(), file.ModifiedTimeUnixSeconds);
    }

    [Fact]
    public void A_link_to_a_file_that_is_not_there_is_left_out()
    {
        if (OperatingSystem.IsWindows())
        {
            // A file link needs a privilege Windows grants only to administrators; the Linux leg runs this.
            return;
        }

        var kept = Write("films", "Kept.mkv");
        File.CreateSymbolicLink(_dir.Join("films", "Dangling.mkv"), _dir.Join("nowhere.mkv"));

        var walked = LibraryFileWalker.Walk(Library(), Folders(_dir.Join("films")));

        Assert.Equal([kept], walked.Select(file => file.Path));
    }
}
