using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// The bare download-client connections surface (#768) over real HTTP: create/list/get/update/delete, the
/// connection test, and the suggestions endpoint — all read only towards the client itself.
/// </summary>
public sealed class DownloadClientConnectionsApiTests
{
    internal const string Connections = "/api/v1/download-clients/connections";
    private const string Suggestions = "/api/v1/download-clients/suggestions";

    internal static async Task<(WeirTestServer Server, ApiTestClient Client, ScriptedManager Manager)> StartAsync()
    {
        var manager = new ScriptedManager();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client, manager);
    }

    internal static async Task<JsonNode> CreateAsync(ApiTestClient client, string kind = "sabnzbd", string apiKey = "key")
    {
        using var response = await client.PostAsync(Connections, new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = kind,
            ["base_url"] = "http://192.0.2.30:8080",
            ["api_key"] = apiKey,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!;
    }

    [Fact]
    public async Task A_connection_is_created_listed_fetched_updated_and_removed_without_ever_exposing_its_secret()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        var created = await CreateAsync(client);
        var id = created["id"]!.GetValue<long>();
        Assert.Equal(("sabnzbd", "SABnzbd on 192.0.2.30"), (created["kind"]!.GetValue<string>(), created["name"]!.GetValue<string>()));
        Assert.True(created["api_key_is_saved"]!.GetValue<bool>());
        Assert.False(created["password_is_saved"]!.GetValue<bool>());
        Assert.Null(created["last_test_ok"]);
        Assert.DoesNotContain("api_key", created.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("password", created.AsObject().Select(p => p.Key));

        var listed = (await Json(await client.GetAsync(Connections)))!.AsArray();
        Assert.Single(listed);

        using (var update = await client.PutAsync($"{Connections}/{id}", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["base_url"] = "http://192.0.2.31:8080",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal("SABnzbd on 192.0.2.31", (await Json(update))!["name"]!.GetValue<string>());
        }

        // The api key is unchanged by an update that does not mention it.
        Assert.True((await Json(await client.GetAsync($"{Connections}/{id}")))!["api_key_is_saved"]!.GetValue<bool>());

        using var deleted = await client.SendAsync(HttpMethod.Delete, $"{Connections}/{id}", new
        {
            csrf_token = await client.CsrfAsync(),
        });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Connections}/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_name_sent_when_creating_or_editing_a_connection_is_accepted_and_ignored()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var created = await client.PostAsync(Connections, new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = "qbittorrent",
            ["name"] = "Living room",
            ["base_url"] = "http://10.0.0.51:8080",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created))!["id"]!.GetValue<long>();

        using var edited = await client.PutAsync($"{Connections}/{id}", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["name"] = "Somewhere else",
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("qBittorrent on 10.0.0.51", (await Json(edited))!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Testing_a_connection_asks_the_client_and_records_the_result()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var created = await CreateAsync(client);
        var id = created["id"]!.GetValue<long>();
        manager.Json(HttpMethod.Get, "/api", """{"config":{"misc":{"complete_dir":"/downloads/complete"}}}""");

        using var response = await client.PostAsync($"{Connections}/{id}/test", new
        {
            csrf_token = await client.CsrfAsync(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await Json(response))!;
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(id, body["connection_id"]!.GetValue<long>());

        var refetched = (await Json(await client.GetAsync($"{Connections}/{id}")))!;
        Assert.True(refetched["last_test_ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Suggestions_need_a_media_type_and_report_one_entry_per_enabled_connection()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);
        manager.Json(HttpMethod.Get, "/api", """{"config":{"misc":{"complete_dir":"/downloads/complete"}}}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(Suggestions)).StatusCode);

        using var response = await client.GetAsync($"{Suggestions}?media_type=movie");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single((await Json(response))!.AsArray())!;
        Assert.Equal("/downloads/complete", entry["suggested_watched_folder"]!.GetValue<string>());
        Assert.Null(entry["ready"]);
        Assert.Null(entry["lines"]);
    }

    [Fact]
    public async Task An_unreachable_connection_is_left_out_of_suggestions_instead_of_failing_the_whole_list()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);
        manager.Route(HttpMethod.Get, "/api", _ => throw new HttpRequestException("refused"));

        using var response = await client.GetAsync($"{Suggestions}?media_type=tv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await Json(response))!.AsArray());
    }
}
