using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="QBittorrentRules"/>: <c>GET /api/v2/torrents/categories</c> and <c>GET /api/v2/app/preferences</c> (#768), read from what a
/// real qBittorrent answered, in every version recorded (<c>fixtures/recorded/qbittorrent</c>).
/// </summary>
public sealed class QBittorrentRulesTests
{
    public static TheoryData<string> Versions => [.. ArrRecording.Versions("qbittorrent")];

    private static WireValue Categories(string version) => WireJsonParser.Parse(ArrRecording.Load("qbittorrent", version, "folders").To("GET", "/api/v2/torrents/categories").Reply.Text);

    private static WireValue Preferences(string version) => WireJsonParser.Parse(ArrRecording.Load("qbittorrent", version, "folders").To("GET", "/api/v2/app/preferences").Reply.Text);

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_default_folder_is_the_preferences_save_path(string version)
    {
        var folders = QBittorrentRules.Parse(Categories(version), Preferences(version));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_category_with_its_own_save_path_is_reported(string version)
    {
        var folders = QBittorrentRules.Parse(Categories(version), Preferences(version));

        Assert.Contains(new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv"), folders.CategoryFolders);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_category_with_an_empty_save_path_is_not_reported_as_a_folder_of_its_own(string version)
    {
        var recorded = ArrRecording.Load("qbittorrent", version, "folders").To("GET", "/api/v2/torrents/categories").Reply.Body!;

        var folders = QBittorrentRules.Parse(Categories(version), Preferences(version));

        Assert.Equal("", (string)recorded["default-recorder"]!["savePath"]!);
        Assert.DoesNotContain(folders.CategoryFolders, category => category.Category == "default-recorder");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void No_categories_object_yields_an_empty_category_list(string version)
    {
        var folders = QBittorrentRules.Parse(null, Preferences(version));

        Assert.Empty(folders.CategoryFolders);
        Assert.Equal("/downloads/complete", folders.CompletedFolder);
    }
}
