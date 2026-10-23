using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>What the watched-folder walk does with names that look like the parent folder, extension lists a person typed, and files that are gone.</summary>
public sealed class WatchedFolderListingEdgeTests
{
    [Theory]
    [InlineData("..Film.mkv")]
    [InlineData("...Film.mkv")]
    public void A_file_whose_name_starts_with_two_dots_is_still_found(string name)
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(watched);
        File.WriteAllBytes(Path.Combine(watched, name), [1]);

        var result = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false);

        Assert.Equal([name], result.Files.Select(Path.GetFileName));
    }

    [Fact]
    public void A_folder_whose_name_starts_with_two_dots_is_still_walked()
    {
        using var dir = new TempDirectory();
        var folder = dir.Join("watch", "..Season 1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Film.mkv"), [1]);

        var result = WatchedFolderListing.Candidates(dir.Join("watch"), null, null, excludeHidden: false, topLevelOnly: false);

        Assert.Equal(["Film.mkv"], result.Files.Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    public void A_file_that_cannot_be_looked_up_is_not_there(string path)
    {
        Assert.Null(WatchedMediaFile.Stat(path));
    }

    [Fact]
    public void A_listed_file_that_has_gone_is_not_there_and_one_that_is_gives_its_size_and_times()
    {
        using var dir = new TempDirectory();
        var present = dir.Join("here.mkv");
        File.WriteAllBytes(present, [1, 2, 3]);

        var file = WatchedMediaFile.From(new FileInfo(present));

        Assert.Equal((present, 3L), (file!.FullPath, file.SizeBytes));
        Assert.Null(WatchedMediaFile.From(new FileInfo(dir.Join("gone.mkv"))));
    }

    [Fact]
    public void A_file_that_is_not_on_disk_is_not_there()
    {
        using var dir = new TempDirectory();

        Assert.Null(WatchedMediaFile.Stat(dir.Join("gone.mkv")));
    }

    [Fact]
    public void A_file_that_is_there_is_read_with_its_size()
    {
        using var dir = new TempDirectory();
        var path = dir.Join("here.mkv");
        File.WriteAllBytes(path, [1, 2, 3]);

        var file = WatchedMediaFile.Stat(path);

        Assert.NotNull(file);
        Assert.Equal(3, file.SizeBytes);
    }

    [Theory]
    [InlineData("Film.mkv", true)]
    [InlineData("Film.MKV", true)]
    [InlineData("Film.txt", false)]
    [InlineData("Film", false)]
    [InlineData("missing.mkv", false)]
    public void Only_an_existing_file_with_a_media_extension_is_a_candidate(string name, bool expected)
    {
        using var dir = new TempDirectory();
        foreach (var existing in new[] { "Film.mkv", "Film.MKV", "Film.txt", "Film" })
        {
            File.WriteAllBytes(dir.Join(existing), [1]);
        }

        Assert.Equal(expected, WatchedFolderListing.IsProcessingMediaCandidate(dir.Join(name)));
    }

    [Fact]
    public void A_folder_is_not_a_media_candidate_even_with_a_media_extension()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("Film.mkv"));

        Assert.False(WatchedFolderListing.IsProcessingMediaCandidate(dir.Join("Film.mkv")));
    }

    [Fact]
    public void Extensions_a_person_typed_are_trimmed_lower_cased_and_given_their_dot()
    {
        var extensions = WatchedFolderListing.ConfiguredExtensions([" MKV ", ".Mp4", "  ", ""]);

        Assert.NotNull(extensions);
        Assert.Equal([".mkv", ".mp4"], extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_extensions_means_the_built_in_list_applies()
    {
        Assert.Null(WatchedFolderListing.ConfiguredExtensions(null));
        Assert.Null(WatchedFolderListing.ConfiguredExtensions([]));
    }

    [Fact]
    public void A_workflows_own_extension_list_replaces_the_built_in_one_and_unknown_types_are_counted()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(watched);
        File.WriteAllBytes(Path.Combine(watched, "a.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(watched, "b.xyz"), [1]);
        File.WriteAllBytes(Path.Combine(watched, "c.xyz"), [1]);
        File.WriteAllBytes(Path.Combine(watched, "d.srt"), [1]);
        File.WriteAllBytes(Path.Combine(watched, "noextension"), [1]);

        var result = WatchedFolderListing.Candidates(watched, ["xyz"], null, excludeHidden: false, topLevelOnly: true);

        Assert.Equal(["b.xyz", "c.xyz"], result.Files.Select(Path.GetFileName));
        Assert.Equal(1, result.IgnoredUnsupportedType);
        Assert.Equal([".mkv"], result.IgnoredUnsupportedExtensions);
    }

    [Fact]
    public void Hidden_folders_and_downloader_folders_are_skipped_when_asked()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        foreach (var folder in new[] { ".hidden", "Incomplete", "Shows" })
        {
            Directory.CreateDirectory(Path.Combine(watched, folder));
            File.WriteAllBytes(Path.Combine(watched, folder, "Film.mkv"), [1]);
        }

        var hiddenKept = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false);
        var hiddenSkipped = WatchedFolderListing.Candidates(watched, null, ["incomplete"], excludeHidden: true, topLevelOnly: false);

        Assert.Equal(2, hiddenKept.Files.Count);
        Assert.Equal(["Shows"], hiddenSkipped.Files.Select(path => Path.GetFileName(Path.GetDirectoryName(path))));
    }

    [Fact]
    public void Subfolders_are_left_alone_when_only_the_top_level_is_wanted()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(Path.Combine(watched, "Sub"));
        File.WriteAllBytes(Path.Combine(watched, "Sub", "Deep.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(watched, "Top.mkv"), [1]);

        var result = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: true);

        Assert.Equal(["Top.mkv"], result.Files.Select(Path.GetFileName));
    }

    [PosixFact("A file link needs a privilege Windows grants only to administrators.")]
    public void A_link_to_a_file_is_listed_with_the_size_of_the_file_it_points_to()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(watched);
        var target = dir.Join("target.mkv");
        File.WriteAllBytes(target, [1, 2, 3, 4, 5]);
        var link = Path.Combine(watched, "Linked.mkv");
        File.CreateSymbolicLink(link, target);

        var result = WatchedFolderListing.Candidates(watched, null, null, excludeHidden: false, topLevelOnly: false);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(5, entry.SizeBytes);
    }
}
