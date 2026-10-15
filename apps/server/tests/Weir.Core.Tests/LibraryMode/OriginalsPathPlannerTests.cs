using Weir.Core.LibraryMode;

namespace Weir.Core.Tests.LibraryMode;

public sealed class OriginalsPathPlannerTests
{
    [Fact]
    public void The_folder_containing_a_file_is_found_among_several()
    {
        var folders = new[] { "/lib/movies", "/lib/shows" };
        Assert.Equal("/lib/shows", OriginalsPathPlanner.ContainingFolder(folders, "/lib/shows/Show/S01E01.mkv"));
    }

    [Fact]
    public void No_folder_containing_the_file_is_null()
    {
        Assert.Null(OriginalsPathPlanner.ContainingFolder(["/lib/movies"], "/elsewhere/film.mkv"));
    }

    [Theory]
    [InlineData("/lib", "/lib", true)]
    [InlineData("/lib", "/lib/film.mkv", true)]
    [InlineData("/lib/", "/lib/film.mkv", true)]
    [InlineData("/lib", "/library/film.mkv", false)]
    [InlineData("/lib", "/lib2/film.mkv", false)]
    [InlineData("/lib", "/lib/../etc/x", false)]
    [InlineData("/lib", "/lib/a/../../x", false)]
    [InlineData("/lib", "/lib/../../lib/x", false)]
    [InlineData("/lib", "/lib/a/../film.mkv", true)]
    [InlineData("/lib", "/lib/../lib/film.mkv", true)]
    [InlineData("/lib", "lib/film.mkv", false)]
    [InlineData("C:\\Media\\Films", "c:/media/films/../Other/film.mkv", false)]
    [InlineData("C:\\Media\\Films", "c:/media/films/Sub/../film.mkv", true)]
    public void IsWithin_matches_the_folder_itself_and_anything_inside_it_only(string folder, string path, bool expected)
    {
        Assert.Equal(expected, OriginalsPathPlanner.IsWithin(folder, path));
    }

    [Fact]
    public void A_kept_original_is_put_at_the_files_normalised_place_below_the_originals_folder()
    {
        Assert.Equal(
            "/lib/.weir-originals/b/film.mkv",
            OriginalsPathPlanner.DestinationPath("/lib", originalsFolder: null, "/lib/a/../b/film.mkv"));
        Assert.Equal(
            "/lib/.weir-originals/b/film.mkv",
            OriginalsPathPlanner.DestinationPath("/lib", originalsFolder: null, "/lib/../lib/b/film.mkv"));
    }

    [Fact]
    public void A_file_that_climbs_out_of_the_library_folder_is_kept_by_its_name_alone()
    {
        Assert.Equal(
            "/lib/.weir-originals/film.mkv",
            OriginalsPathPlanner.DestinationPath("/lib", originalsFolder: null, "/lib/../etc/film.mkv"));
    }

    [Fact]
    public void The_default_destination_folder_is_dot_weir_originals_inside_the_library_folder()
    {
        Assert.Equal("/lib/movies/.weir-originals", OriginalsPathPlanner.DestinationFolder("/lib/movies", originalsFolder: null));
        Assert.Equal("/lib/movies/.weir-originals", OriginalsPathPlanner.DestinationFolder("/lib/movies", originalsFolder: "  "));
    }

    [Fact]
    public void The_default_destination_folder_keeps_a_windows_library_folders_own_separator()
    {
        Assert.Equal(
            @"D:\Media\Movies\.weir-originals",
            OriginalsPathPlanner.DestinationFolder(@"D:\Media\Movies", originalsFolder: null));
    }

    [Fact]
    public void An_explicit_originals_folder_is_used_as_is()
    {
        Assert.Equal("/backups/originals", OriginalsPathPlanner.DestinationFolder("/lib/movies", "/backups/originals"));
    }

    [Fact]
    public void The_destination_path_keeps_the_files_position_relative_to_its_library_folder()
    {
        var destination = OriginalsPathPlanner.DestinationPath("/lib/movies", null, "/lib/movies/Film (2020)/Film (2020).mkv");
        Assert.Equal("/lib/movies/.weir-originals/Film (2020)/Film (2020).mkv", destination);
    }

    [Fact]
    public void The_first_candidate_is_the_path_itself()
    {
        Assert.Equal("/originals/film.mkv", OriginalsPathPlanner.CandidateAt("/originals/film.mkv", 1));
    }

    [Fact]
    public void Later_candidates_add_a_numbered_suffix_before_the_extension()
    {
        Assert.Equal("/originals/film (2).mkv", OriginalsPathPlanner.CandidateAt("/originals/film.mkv", 2));
        Assert.Equal("/originals/film (3).mkv", OriginalsPathPlanner.CandidateAt("/originals/film.mkv", 3));
    }

    [Fact]
    public void A_windows_style_path_keeps_its_own_separator_in_later_candidates()
    {
        Assert.Equal(@"D:\Media\.weir-originals\film (2).mkv", OriginalsPathPlanner.CandidateAt(@"D:\Media\.weir-originals\film.mkv", 2));
    }

    [Fact]
    public void An_attempt_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OriginalsPathPlanner.CandidateAt("/originals/film.mkv", 0));
    }
}
