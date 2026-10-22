using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Real-temp-dir tests of what the watched-folder walk finds under awkward names: capital extensions, long paths and a folder that was never there.</summary>
public sealed class WatchedFolderListingPathTests
{
    [Theory]
    [InlineData("Film.MKV")]
    [InlineData("Film.Mkv")]
    [InlineData("Film.mkv")]
    public void A_media_file_is_found_whatever_the_case_of_its_extension(string name)
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(watched);
        File.WriteAllBytes(Path.Combine(watched, name), [1]);

        var result = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false);

        Assert.Equal([name], result.Files.Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_deeper_than_the_old_260_character_limit_is_found()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        var deep = watched;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        var film = Path.Combine(deep, "Film.mkv");
        File.WriteAllBytes(film, [1]);

        var result = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false);

        Assert.Equal([film], result.Files);
    }

    [Fact]
    public void A_watched_folder_that_does_not_exist_lists_nothing_instead_of_failing()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.Join("Film.mkv"), [1]);

        var result = WatchedFolderListing.Candidates(dir.Join("never-made"), null, null, excludeHidden: false, topLevelOnly: false);

        Assert.Empty(result.Files);
    }
}
