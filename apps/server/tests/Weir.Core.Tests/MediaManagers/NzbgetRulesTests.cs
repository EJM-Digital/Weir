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

        Assert.Equal([new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one")], folders.CategoryFolders);
    }

    [Fact]
    public void A_category_with_no_destination_is_skipped_rather_than_reported_with_a_blank_folder()
    {
        var options = ArrRecording.Load("nzbget", "folders").Only.Reply.Body!["result"]!.AsArray();

        var folders = NzbgetRules.ParseConfig(Config);

        Assert.Contains(options, option => (string?)option!["Name"] == "Category2.Name" && (string?)option["Value"] == "Series");
        Assert.DoesNotContain(options, option => (string?)option!["Name"] == "Category2.DestDir" && !string.IsNullOrEmpty((string?)option["Value"]));
        Assert.DoesNotContain(folders.CategoryFolders, category => category.Category == "Series");
    }

    [Fact]
    public void No_result_array_yields_no_folders_at_all()
    {
        var folders = NzbgetRules.ParseConfig(WireJsonParser.Parse("""{"version":"1.1"}"""));

        Assert.Null(folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
    }
}
