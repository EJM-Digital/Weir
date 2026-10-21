using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.MediaManagers.DownloadClientConnectionsApiTests;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// Download-client connections at their edges: who may use each route, an address or credential Weir refuses when saving or
/// editing, a connection with no address, one of a kind Weir has no way to talk to, and one removed while its test was running.
/// </summary>
public sealed class DownloadClientConnectionEdgesApiTests
{
    private const string NoSuchConnection = "That download client connection does not exist.";
    private const string Suggestions = "/api/v1/download-clients/suggestions";

    private static async Task<HttpResponseMessage> SendAsync(ApiTestClient client, HttpMethod method, string path, Dictionary<string, object?> body)
    {
        body["csrf_token"] = await client.CsrfAsync();
        return await client.SendAsync(method, path, body);
    }

    [Fact]
    public async Task Listing_reading_and_testing_are_for_operators_and_changing_a_connection_is_for_administrators()
    {
        var (server, _, _) = await StartAsync();
        await using var _server = server;

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, Connections, Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, Connections + "/1", Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, Suggestions + "?media_type=movie", Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, Connections + "/1/test", Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, Connections, Callers.Admin);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Put, Connections + "/1", Callers.Admin);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Delete, Connections + "/1", Callers.Admin);
    }

    [Theory]
    [InlineData("sabnzbd", "http://192.0.2.30:8080", null, "SABnzbd needs an API key. Find it in SABnzbd under Config → General.")]
    [InlineData("deluge", "http://192.0.2.30:8112", null, "Deluge needs its Web UI password.")]
    [InlineData("qbittorrent", "not an address", "key", "That address will not work: ")]
    public async Task A_connection_that_cannot_work_is_refused_with_the_reason_and_nothing_is_saved(string kind, string address, string? apiKey, string reason)
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var response = await SendAsync(client, HttpMethod.Post, Connections, new() { ["kind"] = kind, ["base_url"] = address, ["api_key"] = apiKey ?? string.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(reason, await Detail(response), StringComparison.Ordinal);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM download_client_connections"));
    }

    [Fact]
    public async Task A_body_that_is_not_an_object_is_refused()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var response = await client.SendAsync(HttpMethod.Post, Connections, content: TestDatabase.RawJson("[]"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM download_client_connections"));
    }

    [Fact]
    public async Task An_edit_that_would_leave_the_connection_unusable_is_refused_and_changes_nothing()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var id = (await CreateAsync(client))["id"]!.GetValue<long>();

        using var badAddress = await SendAsync(client, HttpMethod.Put, $"{Connections}/{id}", new() { ["base_url"] = "not an address" });
        using var clearedKey = await SendAsync(client, HttpMethod.Put, $"{Connections}/{id}", new() { ["api_key"] = "" });

        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.StartsWith("That address will not work: ", await Detail(badAddress), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, clearedKey.StatusCode);
        Assert.Equal("SABnzbd needs an API key. Find it in SABnzbd under Config → General.", await Detail(clearedKey));
        Assert.Equal("http://192.0.2.30:8080", await TestDatabase.ScalarStringAsync(server, "SELECT base_url FROM download_client_connections WHERE id = $id", ("$id", id)));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task A_connection_that_is_not_there_is_a_404_and_an_id_that_is_not_a_number_is_a_422(string verb)
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var path = verb == "POST" ? Connections + "/{0}/test" : Connections + "/{0}";

        using var missing = await SendAsync(client, new HttpMethod(verb), string.Format(System.Globalization.CultureInfo.InvariantCulture, path, 404), []);
        using var notANumber = await SendAsync(client, new HttpMethod(verb), string.Format(System.Globalization.CultureInfo.InvariantCulture, path, "abc"), []);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(NoSuchConnection, await Detail(missing));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notANumber.StatusCode);
    }

    [Fact]
    public async Task Testing_a_connection_with_no_address_says_to_add_one_and_does_not_dial_out()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        using var created = await SendAsync(client, HttpMethod.Post, Connections, new() { ["kind"] = "qbittorrent" });
        var id = (await Json(created))["id"]!.GetValue<long>();

        using var response = await SendAsync(client, HttpMethod.Post, $"{Connections}/{id}/test", []);

        var body = await Json(response);
        Assert.Equal((false, "Add the address where this app can be reached, then test again."), (body["ok"]!.GetValue<bool>(), body["detail"]!.GetValue<string>()));
        Assert.Empty(manager.Requests);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT last_connection_test_ok FROM download_client_connections WHERE id = $id", ("$id", id)));
    }

    [Fact]
    public async Task Testing_a_connection_of_a_kind_weir_cannot_talk_to_says_so()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var id = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO download_client_connections (kind, name, base_url) VALUES ('mystery', 'Mystery client', 'http://192.0.2.31:9000') RETURNING id");

        using var response = await SendAsync(client, HttpMethod.Post, $"{Connections}/{id}/test", []);

        var body = await Json(response);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.StartsWith("Weir does not know how to talk to ", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(manager.Requests);
    }

    [Fact]
    public async Task A_connection_removed_while_its_test_is_running_is_reported_as_gone_and_leaves_no_record()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var id = (await CreateAsync(client))["id"]!.GetValue<long>();
        manager.Route(
            HttpMethod.Get,
            "/api",
            async _ =>
            {
                await TestDatabase.ExecuteAsync(server, "DELETE FROM download_client_connections WHERE id = $id", ("$id", id));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"config":{"misc":{"complete_dir":"/downloads/complete"}}}""") };
            });

        using var response = await SendAsync(client, HttpMethod.Post, $"{Connections}/{id}/test", []);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("That download client connection was removed while its connection test was running.", await Detail(response));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM download_client_connections"));
    }
}
