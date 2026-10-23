using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

public sealed class LibraryFileWalkerTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static ProcessingLibraryRecord Library() => new()
    {
        Name = "Movies",
        MediaExtensionsCsv = "mkv",
    };

    private void WriteFile(params string[] relativeParts)
    {
        var path = _folder.Join(relativeParts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    [Fact]
    public void A_file_under_the_default_originals_folder_is_never_walked()
    {
        WriteFile("Film.mkv");
        WriteFile(".weir-originals", "Film.mkv");
        var settings = new LibrarySettings([_folder.Path], ScheduleEnabled: false, KeepOriginalAfterClean: true);

        var walked = LibraryFileWalker.Walk(Library(), settings, Weir.Infrastructure.IO.PhysicalFileSystem.Instance);

        Assert.Single(walked);
        Assert.Equal(_folder.Join("Film.mkv"), walked[0].Path);
    }

    [Fact]
    public void A_file_under_an_explicit_originals_folder_inside_the_library_folder_is_never_walked()
    {
        WriteFile("Film.mkv");
        WriteFile("backups", "Film.mkv");
        var settings = new LibrarySettings(
            [_folder.Path], ScheduleEnabled: false, KeepOriginalAfterClean: true, OriginalsFolder: _folder.Join("backups"));

        var walked = LibraryFileWalker.Walk(Library(), settings);

        Assert.Single(walked);
        Assert.Equal(_folder.Join("Film.mkv"), walked[0].Path);
    }

    [Fact]
    public void The_originals_folder_is_excluded_even_when_the_setting_is_off()
    {
        // The folder is excluded on its own path, not on whether #735 is currently on: a library switched off
        // again must not suddenly start treating its own kept originals as library files.
        WriteFile("Film.mkv");
        WriteFile(".weir-originals", "Film.mkv");
        var settings = new LibrarySettings([_folder.Path], ScheduleEnabled: false, KeepOriginalAfterClean: false);

        var walked = LibraryFileWalker.Walk(Library(), settings);

        Assert.Single(walked);
    }

    [Fact]
    public void With_no_originals_folder_configured_nothing_is_excluded()
    {
        WriteFile("Film.mkv");
        WriteFile("Extras", "Trailer.mkv");
        var settings = new LibrarySettings([_folder.Path], ScheduleEnabled: false);

        var walked = LibraryFileWalker.Walk(Library(), settings);

        Assert.Equal(2, walked.Count);
    }
}
