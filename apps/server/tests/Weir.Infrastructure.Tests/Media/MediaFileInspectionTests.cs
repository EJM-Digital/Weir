using Weir.Infrastructure.Media;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>What Weir reads about a path before it asks a media tool to look at it: a file, a folder, nothing, or something that cannot be a path.</summary>
public sealed class MediaFileInspectionTests
{
    [Fact]
    public void A_file_has_its_size_and_a_modified_time()
    {
        using var folder = new TempDirectory();
        var file = folder.Join("film.mkv");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var state = MediaTools.InspectFile(file);

        Assert.True(state.Exists);
        Assert.True(state.IsFile);
        Assert.Equal(4, state.SizeBytes);
        Assert.True(state.MtimeEpoch > 0);
        Assert.Equal(Path.GetFullPath(file), state.ResolvedPath);
    }

    [Fact]
    public void A_folder_exists_but_is_not_a_file_and_has_no_size()
    {
        using var folder = new TempDirectory();

        var state = MediaTools.InspectFile(folder.Path);

        Assert.True(state.Exists);
        Assert.False(state.IsFile);
        Assert.Equal(0, state.SizeBytes);
        Assert.True(state.MtimeEpoch > 0);
    }

    [Fact]
    public void A_path_that_is_not_there_is_reported_as_missing()
    {
        using var folder = new TempDirectory();

        var state = MediaTools.InspectFile(folder.Join("missing.mkv"));

        Assert.False(state.Exists);
        Assert.False(state.IsFile);
        Assert.Equal(-1, state.SizeBytes);
    }

    [Fact]
    public void A_path_that_cannot_be_resolved_is_kept_as_it_was_given()
    {
        var state = MediaTools.InspectFile("bad\0name.mkv");

        Assert.Equal("bad\0name.mkv", state.ResolvedPath);
        Assert.False(state.Exists);
    }
}
