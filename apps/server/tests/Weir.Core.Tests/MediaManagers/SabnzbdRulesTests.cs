using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="SabnzbdRules"/>: SABnzbd's <c>get_config</c> sections for <c>misc</c> and <c>categories</c> (#768), read from what a real
/// SABnzbd answered (<c>fixtures/recorded/sabnzbd</c>). A test that needs a case no run produced builds it and says so.
/// </summary>
public sealed class SabnzbdRulesTests
{
    private static readonly ArrRecording Folders = ArrRecording.Load("sabnzbd", "folders");

    private static WireValue Misc => WireJsonParser.Parse(Folders.Exchanges[0].Reply.Text);

    private static WireValue Categories => WireJsonParser.Parse(Folders.Exchanges[1].Reply.Text);

    [Fact]
    public void The_base_completed_folder_comes_from_the_misc_section()
    {
        Assert.Equal("/downloads/complete", SabnzbdRules.ParseCompleteDir(Misc));
    }

    [Fact]
    public void No_misc_section_yields_no_base_folder() =>
        Assert.Null(SabnzbdRules.ParseCompleteDir(WireJsonParser.Parse("""{"config":{}}""")));

    [Fact]
    public void A_relative_category_folder_is_joined_onto_the_base_folder()
    {
        var folders = SabnzbdRules.ParseCategoryFolders(Categories, SabnzbdRules.ParseCompleteDir(Misc));

        Assert.Contains(new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv"), folders);
    }

    [Fact]
    public void An_absolute_category_folder_replaces_the_base_folder_instead_of_joining_it()
    {
        var folders = SabnzbdRules.ParseCategoryFolders(Categories, SabnzbdRules.ParseCompleteDir(Misc));

        Assert.Contains(new DownloadClientCategoryFolder("elsewhere-recorder", "/media/elsewhere"), folders);
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
        var folders = SabnzbdRules.ParseCategoryFolders(Categories, SabnzbdRules.ParseCompleteDir(Misc));

        var recorded = Folders.Exchanges[1].Reply.Body!["config"]!["categories"]!.AsArray();

        Assert.Equal(["*", "tv-recorder", "elsewhere-recorder", "movies", "tv", "audio", "software"], recorded.Select(row => (string)row!["name"]!));
        Assert.Equal(["", "tv", "/media/elsewhere", "", "", "", ""], recorded.Select(row => (string)row!["dir"]!));
        Assert.DoesNotContain(folders, folder => folder.Category == "*");
    }

    [Fact]
    public void A_category_with_no_folder_saves_into_the_completed_folder_itself()
    {
        var folders = SabnzbdRules.ParseCategoryFolders(Categories, SabnzbdRules.ParseCompleteDir(Misc));

        Assert.Equal(
            [
                new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv"),
                new DownloadClientCategoryFolder("elsewhere-recorder", "/media/elsewhere"),
                new DownloadClientCategoryFolder("movies", "/downloads/complete"),
                new DownloadClientCategoryFolder("tv", "/downloads/complete"),
                new DownloadClientCategoryFolder("audio", "/downloads/complete"),
                new DownloadClientCategoryFolder("software", "/downloads/complete"),
            ],
            folders);
    }

    [Fact]
    public void A_category_with_no_folder_follows_the_default_category_when_that_has_one()
    {
        // The recorded reply with a folder on the default category: no run of a real SABnzbd produced this one.
        var categories = CategoriesWith(("*", "everything"), ("movies", ""), ("tv", "shows"));

        var folders = SabnzbdRules.ParseCategoryFolders(categories, "/downloads/complete");

        Assert.Equal(
            [new DownloadClientCategoryFolder("movies", "/downloads/complete/everything"), new DownloadClientCategoryFolder("tv", "/downloads/complete/shows")],
            folders);
    }

    [Fact]
    public void A_category_with_no_folder_follows_an_absolute_default_category_folder()
    {
        var categories = CategoriesWith(("*", "/media/all"), ("movies", ""));

        var folders = SabnzbdRules.ParseCategoryFolders(categories, "/downloads/complete");

        Assert.Equal([new DownloadClientCategoryFolder("movies", "/media/all")], folders);
    }

    [Fact]
    public void A_category_with_no_folder_is_left_out_when_the_completed_folder_is_unknown()
    {
        var folders = SabnzbdRules.ParseCategoryFolders(CategoriesWith(("movies", "")), null);

        Assert.Empty(folders);
    }

    [Fact]
    public void A_folder_ending_in_an_asterisk_is_the_folder_without_it()
    {
        // The asterisk only tells SABnzbd not to make a folder per job: https://sabnzbd.org/wiki/configuration/5.1/categories
        var folders = SabnzbdRules.ParseCategoryFolders(CategoriesWith(("tv", "shows*")), "/downloads/complete");

        Assert.Equal([new DownloadClientCategoryFolder("tv", "/downloads/complete/shows")], folders);
    }

    private static WireObject CategoriesWith(params (string Name, string Dir)[] rows) =>
        new WireObject().Set("config", new WireObject().Set(
            "categories",
            new WireArray([.. rows.Select(row => (WireValue)new WireObject().Set("name", row.Name).Set("dir", row.Dir))])));
}
