using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// A running Weir tests a download client connection, and suggests the folders the client saves to, against a client that answers as the
/// real qBittorrent, Transmission, Deluge, SABnzbd and NZBGet did when they were recorded (<c>fixtures/recorded/&lt;client&gt;</c>, made by
/// <c>apps/server/tools/Weir.ClientRecorder</c>). The fake decides only whether the secret is the one it expects; every reply it gives is a
/// recorded one.
/// </summary>
[ContractArea("media_managers")]
public sealed class DownloadClientRecordedRepliesTests(DownloadClientServerFixture fixture) : IClassFixture<DownloadClientServerFixture>
{
    private const string Secret = "stored-secret-6c2f";
    private const string Completed = "/downloads/complete";

    private static readonly Dictionary<string, string> Refusals = new()
    {
        ["qbittorrent"] = "the username or password was refused. Check them and save again.",
        ["transmission"] = "the username or password was refused. Check them and save again.",
        ["nzbget"] = "the username or password was refused. Check them and save again.",
        ["sabnzbd"] = "the API key was refused. Check the key and save it again.",
        ["deluge"] = "the password was refused. Check it and save it again.",
    };

    /// <summary>What each client's recording gives as its folders, in the order the client lists them. The fake reports <see cref="Completed"/> as the completed folder.</summary>
    private static readonly Dictionary<string, (string Completed, (string Category, string Folder)[] Categories)> Folders = new()
    {
        ["qbittorrent"] = (Completed, [("elsewhere-recorder", "/media/elsewhere"), ("tv-recorder", "/downloads/complete/tv")]),
        ["transmission"] = (Completed, []),
        ["nzbget"] = (Completed, [("Movies", "/downloads/complete/category-one"), ("Series", "/downloads/complete/Series"), ("Music", "/downloads/complete/Music"), ("Software", "/downloads/complete/Software")]),
        ["sabnzbd"] = (Completed, [("tv-recorder", "/downloads/complete/tv"), ("elsewhere-recorder", "/media/elsewhere"), ("movies", Completed), ("tv", Completed), ("audio", Completed), ("software", Completed)]),
        ["deluge"] = (Completed, [("tv-recorder", "/downloads/complete/tv")]),
    };

    public static IEnumerable<object[]> Kinds() => DownloadClients.KindNames();

    /// <summary>What Weir calls a connection that has no name of its own: the product, and the machine it is on.</summary>
    private static string Label(DownloadClientKind kind, FakeDownloadClient client) => $"{kind.Product} on {new Uri(client.BaseUrl).Host}";

    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await fixture.Server.CreateAdminClientAsync();
        await DownloadClients.ClearAsync(admin);
        return admin;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_test_button_says_connected_when_the_client_lets_the_saved_credentials_in(string kindName)
    {
        var kind = DownloadClients.Kind(kindName);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, Secret);

        var tested = await DownloadClients.TestAsync(admin, connection);

        Assert.True((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal($"Connected. Weir can reach {Label(kind, client)}.", (string)tested["detail"]!);
        Assert.True(client.Attempts.All(attempt => attempt.Accepted));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_test_button_says_in_plain_words_that_the_client_refused_the_saved_credentials(string kindName)
    {
        var kind = DownloadClients.Kind(kindName);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        var connection = await DownloadClients.CreateAsync(admin, kind, client, "a-secret-the-client-does-not-know");

        var tested = await DownloadClients.TestAsync(admin, connection);

        Assert.False((bool)tested["ok"]!, tested.ToJsonString());
        Assert.Equal($"Weir reached {Label(kind, client)}, but {Refusals[kindName]}", (string)tested["detail"]!);
        Assert.Contains(client.Attempts, attempt => !attempt.Accepted);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_suggestions_are_the_folders_the_client_reports(string kindName)
    {
        var kind = DownloadClients.Kind(kindName);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        await DownloadClients.CreateAsync(admin, kind, client, Secret);

        var suggestions = await admin.GetAsync($"{WeirClient.Api}/download-clients/suggestions?media_type=tv");

        var entry = Assert.IsType<JsonObject>(Assert.Single(suggestions.Elements));
        Assert.Equal(kindName, (string)entry["kind"]!);
        Assert.Equal(Folders[kindName].Completed, (string)entry["suggested_watched_folder"]!);
        Assert.Equal(
            Folders[kindName].Categories.Select(pair => (pair.Category, pair.Folder)),
            entry["category_folders"]!.AsArray().Select(row => ((string)row!["category"]!, (string)row["folder"]!)));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_client_that_refuses_the_saved_credentials_contributes_no_suggestion(string kindName)
    {
        var kind = DownloadClients.Kind(kindName);
        using var admin = await OperatorAsync();
        using var client = DownloadClients.StartFake(kind, Secret);
        await DownloadClients.CreateAsync(admin, kind, client, "a-secret-the-client-does-not-know");

        var suggestions = await admin.GetAsync($"{WeirClient.Api}/download-clients/suggestions?media_type=tv");

        Assert.Empty(suggestions.Elements);
    }
}
