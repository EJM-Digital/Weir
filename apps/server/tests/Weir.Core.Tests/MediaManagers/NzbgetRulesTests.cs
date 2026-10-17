using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="NzbgetRules"/>: NZBGet's flat <c>{"Name","Value"}</c> option array from its JSON-RPC <c>config</c> method (#768), read from
/// what a real NZBGet answered (<c>fixtures/recorded/nzbget</c>).
/// </summary>
public sealed class NzbgetRulesTests
{
    private static WireValue Config => WireJsonParser.Parse(ArrRecording.Load("nzbget", "folders").Only.Reply.Text);

    private static Dictionary<string, string> RecordedOptions() =>
        ArrRecording.Load("nzbget", "folders").Only.Reply.Body!["result"]!.AsArray()
            .ToDictionary(option => (string)option!["Name"]!, option => (string)option!["Value"]!);

    private static WireValue WithAppendCategoryDir(string value)
    {
        var body = ArrRecording.Load("nzbget", "folders").Only.Reply.Body!.DeepClone();
        foreach (var option in body["result"]!.AsArray().Where(option => (string?)option!["Name"] == "AppendCategoryDir"))
        {
            option!["Value"] = value;
        }

        return WireJsonParser.Parse(body.ToJsonString());
    }

    [Fact]
    public void The_base_folder_is_DestDir()
    {
        var folders = NzbgetRules.ParseConfig(Config);

        Assert.Equal("/downloads/completed", folders.CompletedFolder);
    }

    [Fact]
    public void Each_numbered_category_is_paired_with_its_own_destination()
    {
        var folders = NzbgetRules.ParseConfig(Config);

        Assert.Contains(new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one"), folders.CategoryFolders);
    }

    [Fact]
    public void A_category_with_no_destination_of_its_own_gets_a_subfolder_of_the_base_folder_named_after_it_when_AppendCategoryDir_is_yes()
    {
        var options = RecordedOptions();

        var folders = NzbgetRules.ParseConfig(Config);

        Assert.Equal("yes", options["AppendCategoryDir"]);
        Assert.Equal(["Movies", "Series", "Music", "Software"], options.Where(option => option.Key.StartsWith("Category", StringComparison.Ordinal) && option.Key.EndsWith(".Name", StringComparison.Ordinal)).Select(option => option.Value));
        Assert.Equal("", options.GetValueOrDefault("Category2.DestDir", ""));
        Assert.Equal(
            [
                new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one"),
                new DownloadClientCategoryFolder("Series", "/downloads/completed/Series"),
                new DownloadClientCategoryFolder("Music", "/downloads/completed/Music"),
                new DownloadClientCategoryFolder("Software", "/downloads/completed/Software"),
            ],
            folders.CategoryFolders);
    }

    [Fact]
    public void A_category_with_no_destination_of_its_own_saves_into_the_base_folder_itself_when_AppendCategoryDir_is_no()
    {
        // The recorded reply with its AppendCategoryDir switched to "no": no run of a real NZBGet produced this one.
        var folders = NzbgetRules.ParseConfig(WithAppendCategoryDir("no"));

        Assert.Equal(
            [
                new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one"),
                new DownloadClientCategoryFolder("Series", "/downloads/completed"),
                new DownloadClientCategoryFolder("Music", "/downloads/completed"),
                new DownloadClientCategoryFolder("Software", "/downloads/completed"),
            ],
            folders.CategoryFolders);
    }

    [Fact]
    public void A_category_with_its_own_destination_is_never_given_its_name_appended()
    {
        var folders = NzbgetRules.ParseConfig(Config);

        Assert.Contains(new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one"), folders.CategoryFolders);
        Assert.DoesNotContain(folders.CategoryFolders, category => category.Folder.EndsWith("/Movies", StringComparison.Ordinal));
    }

    [Fact]
    public void A_category_with_no_destination_is_left_out_when_the_base_folder_is_unknown()
    {
        var reply = """{"result":[{"Name":"AppendCategoryDir","Value":"yes"},{"Name":"Category1.Name","Value":"Series"}]}""";

        var folders = NzbgetRules.ParseConfig(WireJsonParser.Parse(reply));

        Assert.Empty(folders.CategoryFolders);
    }

    [Fact]
    public void A_category_name_that_climbs_out_of_the_base_folder_is_left_out()
    {
        var reply = """{"result":[{"Name":"DestDir","Value":"/downloads/completed"},{"Name":"Category1.Name","Value":"../etc"}]}""";

        var folders = NzbgetRules.ParseConfig(WireJsonParser.Parse(reply));

        Assert.Empty(folders.CategoryFolders);
    }

    [Fact]
    public void No_result_array_yields_no_folders_at_all()
    {
        var folders = NzbgetRules.ParseConfig(WireJsonParser.Parse("""{"version":"1.1"}"""));

        Assert.Null(folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
    }
}
