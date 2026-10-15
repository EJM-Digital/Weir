using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary><see cref="SabnzbdRules"/>: SABnzbd's <c>get_config</c> sections for <c>misc</c> and <c>categories</c> (#768).</summary>
public sealed class SabnzbdRulesTests
{
    private const string Misc = """
        {"config":{"misc":{"complete_dir":"/downloads/complete","download_dir":"/downloads/incomplete"}}}
        """;

    private const string Categories = """
        {"config":{"categories":[
          {"name":"*","dir":"","priority":0},
          {"name":"tv-sonarr","dir":"tv","priority":0},
          {"name":"movies","dir":"/media/movies-complete","priority":0}
        ]}}
        """;

    [Fact]
    public void The_base_completed_folder_comes_from_the_misc_section()
    {
        Assert.Equal("/downloads/complete", SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse(Misc)));
    }

    [Fact]
    public void No_misc_section_yields_no_base_folder() =>
        Assert.Null(SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse("""{"config":{}}""")));

    [Fact]
    public void A_relative_category_folder_is_joined_onto_the_base_folder()
    {
        var completeDir = SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse(Misc));
        var folders = SabnzbdRules.ParseCategoryFolders(WireJsonParser.Parse(Categories), completeDir);

        Assert.Equal(new DownloadClientCategoryFolder("tv-sonarr", "/downloads/complete/tv"), folders[0]);
    }

    [Fact]
    public void An_absolute_category_folder_replaces_the_base_folder_instead_of_joining_it()
    {
        var completeDir = SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse(Misc));
        var folders = SabnzbdRules.ParseCategoryFolders(WireJsonParser.Parse(Categories), completeDir);

        Assert.Equal(new DownloadClientCategoryFolder("movies", "/media/movies-complete"), folders[1]);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("tv/../../etc")]
    [InlineData("/media/../etc")]
    [InlineData("C:\\Media\\..\\Windows")]
    public void A_category_folder_that_climbs_out_with_a_parent_segment_is_left_out(string dir)
    {
        var bad = new WireObject().Set("name", "bad").Set("dir", dir);
        var good = new WireObject().Set("name", "tv").Set("dir", "tv");
        var categories = new WireObject().Set("config", new WireObject().Set("categories", new WireArray([bad, good])));

        var folders = SabnzbdRules.ParseCategoryFolders(categories, "/downloads/complete");

        Assert.Equal([new DownloadClientCategoryFolder("tv", "/downloads/complete/tv")], folders);
    }

    [Fact]
    public void The_default_category_is_not_reported_as_a_folder_of_its_own()
    {
        var completeDir = SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse(Misc));
        var folders = SabnzbdRules.ParseCategoryFolders(WireJsonParser.Parse(Categories), completeDir);

        Assert.DoesNotContain(folders, folder => folder.Category == "*");
        Assert.Equal(2, folders.Count);
    }
}
