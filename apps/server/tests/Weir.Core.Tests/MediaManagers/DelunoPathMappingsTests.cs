using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary><see cref="DelunoPathMappings.Apply"/>: a Deluno folder turned into Weir's view through Deluno's processor path mappings.</summary>
public sealed class DelunoPathMappingsTests
{
    private static MappedPath Apply(string path, params (string Deluno, string Weir)[] mappings) =>
        DelunoPathMappings.Apply([.. mappings.Select(mapping => new DelunoPathMapping(mapping.Deluno, mapping.Weir))], path);

    [Fact]
    public void A_folder_inside_a_mapped_folder_takes_the_weir_side_prefix()
    {
        var mapped = Apply("/mnt/nas/complete/tv", ("/mnt/nas", "/media"));

        Assert.Equal("/media/complete/tv", mapped.Path);
        Assert.Equal("/mnt/nas/complete/tv", mapped.Original);
        Assert.True(mapped.WasMapped);
    }

    [Fact]
    public void A_mapping_onto_the_weir_side_filesystem_root_keeps_the_folders_below_the_mapped_one()
    {
        var mapped = Apply("/mnt/nas/complete/tv", ("/mnt/nas", "/"));

        Assert.Equal("/complete/tv", mapped.Path);
    }

    [Fact]
    public void A_folder_no_mapping_covers_is_left_as_it_is()
    {
        var mapped = Apply("/srv/other", ("/mnt/nas", "/media"));

        Assert.Equal("/srv/other", mapped.Path);
        Assert.Null(mapped.Via);
        Assert.False(mapped.WasMapped);
    }

    [Fact]
    public void With_no_mappings_at_all_the_folder_is_left_as_it_is()
    {
        Assert.Equal("C:\\Downloads\\Movies", Apply("C:\\Downloads\\Movies").Path);
    }

    [Fact]
    public void The_longest_matching_mapping_wins_whatever_its_position()
    {
        var mapped = Apply("/mnt/nas/complete/tv", ("/mnt", "/a"), ("/mnt/nas/complete", "/b"), ("/mnt/nas", "/c"));

        Assert.Equal("/b/tv", mapped.Path);
        Assert.Equal("/mnt/nas/complete", mapped.Via!.DelunoPath);
    }

    [Fact]
    public void Two_mappings_of_the_same_length_resolve_to_the_higher_priority_one()
    {
        var mapped = Apply("/mnt/nas/tv", ("/mnt/nas", "/first"), ("/MNT/NAS", "/second"));

        Assert.Equal("/first/tv", mapped.Path);
    }

    [Fact]
    public void Windows_paths_match_without_regard_to_case_and_keep_the_rest_as_written()
    {
        var mapped = Apply("c:\\nasmount\\Completed\\Movies", ("C:\\NasMount", "D:\\Downloads"));

        Assert.Equal("D:\\Downloads\\Completed\\Movies", mapped.Path);
    }

    [Fact]
    public void Unix_paths_match_only_with_the_same_case()
    {
        var mapped = Apply("/Mnt/nas/tv", ("/mnt/nas", "/media"));

        Assert.False(mapped.WasMapped);
    }

    [Fact]
    public void Separators_are_normalised_to_the_weir_side_and_a_trailing_separator_does_not_matter()
    {
        var toWindows = Apply("/mnt/nas/complete/tv/", ("/mnt/nas/", "C:\\Media\\"));
        var toUnix = Apply("C:\\NasMount\\Completed\\Movies", ("C:/NasMount", "/media"));

        Assert.Equal("C:\\Media\\complete\\tv", toWindows.Path);
        Assert.Equal("/media/Completed/Movies", toUnix.Path);
    }

    [Fact]
    public void The_mapped_folder_itself_maps_to_the_weir_folder()
    {
        Assert.Equal("D:\\Downloads", Apply("C:\\NasMount", ("C:\\NasMount", "D:\\Downloads")).Path);
    }

    [Fact]
    public void A_sibling_folder_that_only_shares_the_text_of_the_prefix_is_not_inside_it()
    {
        Assert.False(Apply("/mnt/nas2/tv", ("/mnt/nas", "/media")).WasMapped);
    }

    [Fact]
    public void A_unc_folder_maps_like_any_other_windows_folder()
    {
        var mapped = Apply("\\\\nas\\share\\Completed", ("\\\\NAS\\Share", "Z:\\"));

        Assert.Equal("Z:\\Completed", mapped.Path);
    }
}
