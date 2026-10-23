using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>The last edges of the connection and repair routes: sign-in details given in every combination, bodies of the wrong kind, and a repair with nothing left to do.</summary>
public sealed class MediaManagerEdgesApiTests
{
    private const string DownloadClients = "/api/v1/download-clients/connections";
    private const string MediaManagers = "/api/v1/media-managers/connections";

    [Theory]
    [InlineData("alice", "secret", "", true, true, false)]
    [InlineData("", "", "key", false, false, true)]
    [InlineData("alice", "", "", true, false, false)]
    [InlineData("", "secret", "", false, true, false)]
    public async Task A_download_client_keeps_exactly_the_sign_in_details_it_was_given(string username, string password, string apiKey, bool hasUser, bool hasPassword, bool hasKey)
    {
        var (server, client, _) = await DownloadClientConnectionsApiTests.StartAsync();
        await using var _server = server;

        using var response = await client.PostAsync(
            DownloadClients,
            new Dictionary<string, object?>
            {
                ["csrf_token"] = await client.CsrfAsync(),
                ["kind"] = "qbittorrent",
                ["base_url"] = "http://192.0.2.30:8080",
                ["username"] = username,
                ["password"] = password,
                ["api_key"] = apiKey,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal((hasUser, hasPassword, hasKey), (!string.IsNullOrEmpty(body["username"]?.GetValue<string>()), body["password_is_saved"]!.GetValue<bool>(), body["api_key_is_saved"]!.GetValue<bool>()));
    }

    [Theory]
    [InlineData(DownloadClients)]
    [InlineData(MediaManagers)]
    public async Task A_new_connection_sent_as_a_list_instead_of_an_object_is_refused(string path)
    {
        var (server, client, _) = await DownloadClientConnectionsApiTests.StartAsync();
        await using var _server = server;

        using var response = await client.PostAsync(path, Array.Empty<object>());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_repair_of_a_temp_artifact_that_is_already_gone_says_nothing_was_applied()
    {
        var (server, client, _) = await DownloadClientConnectionsApiTests.StartAsync();
        await using var _server = server;
        var work = Path.Join(server.Home, "work");
        Directory.CreateDirectory(work);
        await TestDatabase.ExecuteAsync(server, "INSERT INTO libraries (name, media_type, work_folder) VALUES ('Films', 'movie', $w)", ("$w", work));

        using var response = await client.PostAsync(
            "/api/v1/system/reconciliation/repair",
            new { action = "remove_processing_temp_artifact", path = Path.Join(work, ".gone.mkv.partial"), confirm = true, csrf_token = await client.CsrfAsync() });

        Assert.Equal("""{"applied":false,"message":"Temp artifact is already gone."}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE detail = 'remove_processing_temp_artifact: Temp artifact is already gone.'"));
    }
}
