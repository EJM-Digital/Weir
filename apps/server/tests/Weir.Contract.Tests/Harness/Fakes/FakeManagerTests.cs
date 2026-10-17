using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Checks the fake media manager over real HTTP, so the scenarios that script it can rely on it.</summary>
[ContractArea("harness")]
public sealed class FakeManagerTests : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task A_deluno_fake_answers_its_integration_surface_and_accepts_processor_events()
    {
        using var deluno = FakeManager.StartDeluno(
            libraries: [new JsonObject { ["id"] = "5f2c0a9e" }], capabilities: ["processor-reject-regrab"]);
        deluno.Jobs.Add(new JsonObject { ["id"] = "job-1" });

        var health = await Json(deluno, HttpMethod.Get, "/api/integrations/external/health");
        var manifest = await Json(deluno, HttpMethod.Get, "/api/integrations/external/manifest");
        var queue = await Json(deluno, HttpMethod.Get, "/api/integrations/external/queue");
        var events = await Send(deluno, HttpMethod.Post, "/api/integrations/processors/events", """{"handoffId":"h1"}""");

        Assert.Equal("ok", (string)health["status"]!);
        Assert.Equal("Deluno", (string)manifest["name"]!);
        Assert.Equal("5f2c0a9e", (string)manifest["libraries"]![0]!["id"]!);
        Assert.Equal("processor-reject-regrab", (string)manifest["capabilities"]![0]!);
        Assert.Equal("job-1", (string)queue["jobs"]![0]!["id"]!);
        Assert.Empty(queue["dispatches"]!.AsArray());
        Assert.Equal(HttpStatusCode.Accepted, events.StatusCode);
        Assert.True((bool)JsonNode.Parse(await events.Content.ReadAsStringAsync())!["accepted"]!);
        var recorded = Assert.Single(deluno.RequestsTo("POST", "/api/integrations/processors/events"));
        Assert.Equal("h1", (string)recorded.Json!["handoffId"]!);
    }

    [Fact]
    public async Task A_fake_that_stopped_answering_drops_every_request_and_keeps_its_port()
    {
        using var deluno = FakeManager.StartDeluno();
        var port = new Uri(deluno.BaseUrl).Port;

        deluno.StopAnswering();

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync(new Uri(deluno.BaseUrl + "/api/integrations/external/health")));
        Assert.Empty(deluno.Requests);
        using var other = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        Assert.Throws<System.Net.Sockets.SocketException>(other.Start);
    }

    [Fact]
    public async Task A_deluno_fake_reflects_libraries_the_test_changes_after_it_started()
    {
        using var deluno = FakeManager.StartDeluno();

        deluno.Libraries.Add(new JsonObject { ["id"] = "a" });
        var first = await Json(deluno, HttpMethod.Get, "/api/integrations/external/manifest");
        deluno.Libraries.Replace([]);
        var second = await Json(deluno, HttpMethod.Get, "/api/integrations/external/manifest");

        Assert.Single(first["libraries"]!.AsArray());
        Assert.Empty(second["libraries"]!.AsArray());
    }

    [Theory]
    [InlineData("radarr", "Radarr", "/api/v3/movie")]
    [InlineData("sonarr", "Sonarr", "/api/v3/episodefile")]
    public async Task An_arr_fake_answers_status_root_folders_queue_command_and_library(string kind, string appName, string libraryPath)
    {
        using var arr = FakeManager.StartArr(kind, rootFolders: ["/a", "/b"]);
        arr.Queue.Add(new JsonObject { ["id"] = 7, ["title"] = "x" });
        arr.Library.Add(new JsonObject { ["id"] = 1 });

        var status = await Json(arr, HttpMethod.Get, "/api/v3/system/status");
        var folders = await Json(arr, HttpMethod.Get, "/api/v3/rootfolder");
        var queue = await Json(arr, HttpMethod.Get, "/api/v3/queue");
        var command = await Send(arr, HttpMethod.Post, "/api/v3/command", """{"name":"RescanMovie"}""");
        var manualImport = await Json(arr, HttpMethod.Get, "/api/v3/manualimport");
        var library = await Json(arr, HttpMethod.Get, libraryPath);

        Assert.Equal(appName, (string)status["appName"]!);
        Assert.Equal(["/a", "/b"], folders.AsArray().Select(folder => (string)folder!["path"]!));
        Assert.Equal([1, 2], folders.AsArray().Select(folder => (int)folder!["id"]!));
        Assert.Equal(1, (int)queue["totalRecords"]!);
        Assert.Equal(1000, (int)queue["pageSize"]!);
        Assert.Equal("x", (string)queue["records"]![0]!["title"]!);
        Assert.Equal(HttpStatusCode.Created, command.StatusCode);
        Assert.Equal("RescanMovie", (string)JsonNode.Parse(await command.Content.ReadAsStringAsync())!["name"]!);
        Assert.Empty(manualImport.AsArray());
        Assert.Equal(1, (int)library[0]!["id"]!);
    }

    [Fact]
    public async Task An_arr_fake_deletes_a_queue_item_once_and_records_how_it_was_asked()
    {
        using var arr = FakeManager.StartArr("radarr");
        arr.Queue.Add(new JsonObject { ["id"] = 4242 });

        var removed = await Send(arr, HttpMethod.Delete, "/api/v3/queue/4242?removeFromClient=true&blocklist=true&skipped=");
        var again = await Send(arr, HttpMethod.Delete, "/api/v3/queue/4242");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(0, arr.Queue.Count);
        var request = arr.RequestsTo("DELETE", "/api/v3/queue/4242")[0];
        Assert.Equal(new Dictionary<string, string[]> { ["removeFromClient"] = ["true"], ["blocklist"] = ["true"] }, request.Query);
    }

    [Fact]
    public async Task A_request_with_no_route_gets_404_and_a_later_route_wins_over_an_earlier_one()
    {
        using var arr = FakeManager.StartArr("sonarr");

        var unknown = await Send(arr, HttpMethod.Get, "/nothing/here");
        arr.Route("GET", "/api/v3/system/status", new Reply(500));
        var overridden = await Send(arr, HttpMethod.Get, "/api/v3/system/status");
        arr.Route("GET", "/api/v3/queue/{id}/details", request => Reply.Ok(new JsonObject { ["path"] = request.Path }));
        var placeholder = await Json(arr, HttpMethod.Get, "/api/v3/queue/12/details");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, overridden.StatusCode);
        Assert.Equal("/api/v3/queue/12/details", (string)placeholder["path"]!);
        var noPlaceholderMatch = await Send(arr, HttpMethod.Get, "/api/v3/queue/12/34/details");
        Assert.Equal(HttpStatusCode.NotFound, noPlaceholderMatch.StatusCode);
    }

    [Fact]
    public async Task A_responder_that_throws_answers_500_instead_of_hanging()
    {
        using var arr = FakeManager.StartArr("radarr");
        arr.Route("GET", "/boom", _ => throw new InvalidOperationException("scripted"));

        var response = await Send(arr, HttpMethod.Get, "/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("scripted", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_recorded_with_their_headers_and_body_and_can_be_waited_for()
    {
        using var deluno = FakeManager.StartDeluno();
        var waiting = deluno.WaitForRequestAsync(
            "POST", "/api/integrations/processors/events", where: request => (string)request.Json!["status"]! == "failed");

        using var message = new HttpRequestMessage(HttpMethod.Post, deluno.BaseUrl + "/api/integrations/processors/events")
        {
            Content = new StringContent("""{"status":"started"}""", Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Api-Key", FakeManager.DefaultApiKey);
        await _http.SendAsync(message);
        Assert.False(waiting.IsCompleted);
        await Send(deluno, HttpMethod.Post, "/api/integrations/processors/events", """{"status":"failed"}""");

        var found = Assert.Single(await waiting);
        Assert.Equal("failed", (string)found.Json!["status"]!);
        Assert.Equal(FakeManager.DefaultApiKey, deluno.RequestsTo("POST", "/api/integrations")[0].Header("x-api-key"));
        Assert.Equal(2, deluno.RequestsTo("post", "/api/integrations/processors").Count);
    }

    [Fact]
    public async Task Waiting_for_a_request_that_never_comes_fails_naming_what_was_seen()
    {
        using var deluno = FakeManager.StartDeluno();
        await Send(deluno, HttpMethod.Get, "/api/integrations/external/health");

        var failure = await Assert.ThrowsAsync<XunitException>(
            () => deluno.WaitForRequestAsync("POST", "/never", timeout: TimeSpan.FromMilliseconds(600)));

        Assert.Contains("POST /never", failure.Message, StringComparison.Ordinal);
        Assert.Contains("GET /api/integrations/external/health", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fake_that_has_been_disposed_no_longer_answers()
    {
        var deluno = FakeManager.StartDeluno();
        var address = deluno.BaseUrl;
        deluno.Dispose();

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync(address + "/api/integrations/external/health"));
    }

    private async Task<HttpResponseMessage> Send(FakeHttpServer fake, HttpMethod method, string pathAndQuery, string? json = null)
    {
        using var message = new HttpRequestMessage(method, fake.BaseUrl + pathAndQuery);
        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(message);
    }

    private async Task<JsonNode> Json(FakeHttpServer fake, HttpMethod method, string pathAndQuery)
    {
        using var response = await Send(fake, method, pathAndQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }
}
