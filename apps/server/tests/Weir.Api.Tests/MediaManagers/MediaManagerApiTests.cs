using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core;
using Weir.Core.Configuration;
using Weir.Core.Processing;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>A scripted manager for the test server's outbound HTTP.</summary>
internal sealed class ScriptedManager : IManagerHttpHandlerFactory
{
    private readonly Dictionary<(string Method, string Path), Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new();

    private readonly List<ManagerCall> _calls = [];
    private ConnectionActivityHub? _activity;
    private TimeProvider _time = TimeProvider.System;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Every request in the order it arrived, its body read as it was sent (the request itself is disposed once the call ends).</summary>
    public IReadOnlyList<ManagerCall> Calls
    {
        get
        {
            lock (Requests)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Report the marked calls to <paramref name="activity"/>, as the real transport does.</summary>
    public ScriptedManager ReportingTo(ConnectionActivityHub activity, TimeProvider time)
    {
        _activity = activity;
        _time = time;
        return this;
    }

    public ScriptedManager Route(HttpMethod method, string path, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        _routes[(method.Method, path)] = respond;
        return this;
    }

    public ScriptedManager Json(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Route(method, path, _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) }));

    public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) =>
        _activity is null ? new Recording(this) : new ConnectionActivityHandler(new Recording(this), _activity, _time);

    private sealed class Recording(ScriptedManager owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : string.Empty;
            request.Headers.TryGetValues("X-Api-Key", out var apiKeys);
            lock (owner.Requests)
            {
                owner.Requests.Add(request);
                owner._calls.Add(new ManagerCall(request.Method.Method, request.RequestUri!.PathAndQuery, apiKeys?.SingleOrDefault(), body));
            }

            return owner._routes.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var respond)
                ? await respond(request)
                : throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        }
    }
}

/// <summary>One request a <see cref="ScriptedManager"/> received.</summary>
internal sealed record ManagerCall(string Method, string PathAndQuery, string? ApiKey, string Body);

/// <summary>
/// Media manager connections, capabilities, intake, hand-off status and reconciliation over the real HTTP
/// pipeline, plus the #527 fix.
/// </summary>
public sealed class MediaManagerApiTests
{
    private const string Connections = "/api/v1/media-managers/connections";

    private static async Task<(WeirTestServer Server, ApiTestClient Client, ScriptedManager Manager)> StartAsync(params (string Name, string Value)[] variables)
    {
        var manager = new ScriptedManager();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", ""), .. variables],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        var trusted = variables.FirstOrDefault(v => v.Name == "WEIR_TRUSTED_BROWSER_ORIGINS").Value;
        using var login = await client.PostAsync(
            "/api/v1/auth/login",
            new { username = "alice", password = AdminPassword, csrf_token = await client.CsrfAsync() },
            trusted is null ? null : new Dictionary<string, string> { ["Origin"] = trusted, ["X-Requested-With"] = "XMLHttpRequest" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (server, client, manager);
    }

    private static async Task<JsonNode> CreateAsync(ApiTestClient client, string kind = "deluno", string baseUrl = "http://192.0.2.10:5099", string apiKey = "deluno_secret_key")
    {
        using var response = await client.PostAsync(Connections, new { csrf_token = await client.CsrfAsync(), kind, base_url = baseUrl, api_key = apiKey });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task Delunos_own_create_request_is_accepted_and_the_name_it_sends_is_ignored()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var created = await client.PostAsync(
            Connections,
            new { csrf_token = await client.CsrfAsync(), kind = "deluno", name = "Deluno", base_url = "http://192.0.2.10:5099", api_key = "deluno_secret_key", enabled = true });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Deluno on 192.0.2.10", (await Json(created))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_name_sent_when_editing_a_connection_is_ignored()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);

        using var edited = await client.PutAsync($"{Connections}/1", new { csrf_token = await client.CsrfAsync(), name = "Something else", enabled = false });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await Json(edited);
        Assert.Equal(("Deluno on 192.0.2.10", false), (body["name"]!.GetValue<string>(), body["enabled"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Connections_of_one_kind_on_the_same_host_are_told_apart_by_port()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client, "radarr", "http://nas:7878");
        await CreateAsync(client, "radarr", "http://nas:7879");
        await CreateAsync(client, "sonarr", "http://nas:8989");

        using var listed = await client.GetAsync(Connections);

        Assert.Equal(
            ["Radarr on nas (7878)", "Radarr on nas (7879)", "Sonarr on nas"],
            (await Json(listed)).AsArray().Select(row => row!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_connection_is_renamed_after_the_address_it_is_moved_to_and_after_a_neighbour_is_removed()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client, "radarr", "http://nas:7878");
        await CreateAsync(client, "radarr", "http://nas:7879");

        using (var removed = await client.SendAsync(HttpMethod.Delete, $"{Connections}/1", new { csrf_token = await client.CsrfAsync() }))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var remaining = await client.GetAsync($"{Connections}/2");
        Assert.Equal("Radarr on nas", (await Json(remaining))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_connection_is_added_listed_updated_and_deleted_without_ever_returning_its_key()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var row = await CreateAsync(client);
        Assert.Equal(
            """{"id":1,"kind":"deluno","name":"Deluno on 192.0.2.10","nickname":null,"enabled":true,"base_url":"http://192.0.2.10:5099","api_key_is_saved":true,"webhook_secret_is_set":false,"webhook_url_path":"/api/v1/intake/webhook/deluno","unsigned_webhook_warning":"This connection accepts webhooks without a secret. Create a secret and add it to Deluno on 192.0.2.10.","last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null,"downloaded_scan_enabled":false}""",
            row.ToJsonString());
        await CreateAsync(client, "radarr", "http://192.0.2.20:7878");

        using (var unknown = await client.PostAsync(Connections, new { csrf_token = await client.CsrfAsync(), kind = "plex", name = "P" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            Assert.Equal("literal_error", (await Json(unknown))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using (var badUrl = await client.PostAsync(Connections, new { csrf_token = await client.CsrfAsync(), kind = "radarr", name = "X", base_url = "not-a-url" }))
        {
            Assert.Contains("will not work", await Detail(badUrl), StringComparison.Ordinal);
        }

        using (var noToken = await client.PostAsync(Connections, new { csrf_token = "bad", kind = "radarr", name = "Y" }))
        {
            Assert.Equal((HttpStatusCode.BadRequest, "Invalid or expired CSRF token."), (noToken.StatusCode, await Detail(noToken)));
        }

        using (var extra = await client.PostAsync(Connections, new { csrf_token = await client.CsrfAsync(), kind = "radarr", name = "Z", surprise = 1 }))
        {
            Assert.Equal("extra_forbidden", (await Json(extra))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using var listed = await client.GetAsync(Connections);
        Assert.Equal(["deluno", "radarr"], (await Json(listed)).AsArray().Select(r => r!["kind"]!.GetValue<string>()));

        using (var moved = await client.PutAsync($"{Connections}/1", new { csrf_token = await client.CsrfAsync(), base_url = "http://192.0.2.11:5099" }))
        {
            var body = await Json(moved);
            Assert.Equal(("Deluno on 192.0.2.11", true), (body["name"]!.GetValue<string>(), body["api_key_is_saved"]!.GetValue<bool>()));
        }

        using (var cleared = await client.PutAsync($"{Connections}/1", new { csrf_token = await client.CsrfAsync(), api_key = "" }))
        {
            Assert.False((await Json(cleared))["api_key_is_saved"]!.GetValue<bool>());
        }

        using (var missing = await client.GetAsync($"{Connections}/99"))
        {
            Assert.Equal((HttpStatusCode.NotFound, "That media manager connection does not exist."), (missing.StatusCode, await Detail(missing)));
        }

        using (var zero = await client.GetAsync($"{Connections}/0"))
        {
            Assert.Equal("greater_than_equal", (await Json(zero))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using var deleted = await client.SendAsync(HttpMethod.Delete, $"{Connections}/2", new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    /// <summary>
    /// A connection whose stored address predates today's validation (or arrived through a restore): flipping
    /// it on must still be refused, the same way saving a bad address is. Only the address is bypassed here to
    /// set up the scenario; the request under test is a normal PUT.
    /// </summary>
    [Fact]
    public async Task Enabling_a_connection_re_validates_its_stored_address()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);
        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_connections SET base_url = 'not-a-url', enabled = 0 WHERE id = 1");

        using var enabled = await client.PutAsync($"{Connections}/1", new { csrf_token = await client.CsrfAsync(), enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, enabled.StatusCode);
        Assert.Contains("will not work", await Detail(enabled), StringComparison.Ordinal);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT enabled FROM media_manager_connections WHERE id = 1"));
    }

    [Fact]
    public async Task Routes_need_an_operator_session()
    {
        var (server, _, _) = await StartAsync();
        await using var _server = server;
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiTestClient(server).GetAsync(Connections)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiTestClient(server).GetAsync("/api/v1/media-managers/capabilities")).StatusCode);
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Connections)).StatusCode);
    }

    /// <summary>
    /// Searching belongs to the media manager, so a connection has no search lanes. An older client that still saves one is
    /// answered with the connection as it is, whatever it sends, and nothing is kept.
    /// </summary>
    [Fact]
    public async Task An_older_client_saving_a_lane_is_answered_with_the_connection_and_nothing_is_kept()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);
        object Lane(string days, string start, string end) => new
        {
            csrf_token = client.CsrfAsync().Result,
            enabled = true,
            max_items_per_run = 25,
            retry_delay_minutes = 60,
            schedule_enabled = true,
            schedule_days = days,
            schedule_start = start,
            schedule_end = end,
            schedule_interval_seconds = 900,
        };
        var connection = await Json(await client.GetAsync($"{Connections}/1"));
        Assert.False(connection.AsObject().ContainsKey("lanes"));

        using (var saved = await client.PutAsync($"{Connections}/1/lanes/missing", Lane("Mon,Tue", "1:00", "5:00")))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(connection.ToJsonString(), (await Json(saved)).ToJsonString());
        }

        // The values are never read, so a day or a time that would once have been refused is not.
        using (var unreadable = await client.PutAsync($"{Connections}/1/lanes/upgrade", Lane("monday", "25:00", "9")))
        {
            Assert.Equal(HttpStatusCode.OK, unreadable.StatusCode);
        }

        using (var other = await client.PutAsync($"{Connections}/1/lanes/missing", new { csrf_token = await client.CsrfAsync(), surprise = 1 }))
        {
            Assert.Equal("extra_forbidden", (await Json(other))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using (var badLane = await client.PutAsync($"{Connections}/1/lanes/sideways", Lane("Mon", "01:00", "05:00")))
        {
            Assert.Equal(("literal_error", "path"), ((await Json(badLane))["detail"]![0]!["type"]!.GetValue<string>(), (await Json(badLane))["detail"]![0]!["loc"]![0]!.GetValue<string>()));
        }

        using (var noConnection = await client.PutAsync($"{Connections}/99/lanes/missing", Lane("Mon", "01:00", "05:00")))
        {
            Assert.Equal(HttpStatusCode.NotFound, noConnection.StatusCode);
        }

        using var again = await client.GetAsync($"{Connections}/1");
        Assert.Equal(connection.ToJsonString(), (await Json(again)).ToJsonString());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM sqlite_master WHERE name = 'media_manager_search_lanes'"));
    }

    [Fact]
    public async Task A_generated_secret_is_shown_once_and_the_webhook_then_enforces_it_for_that_manager_only()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client);
        await CreateAsync(client, "radarr", "http://192.0.2.20:7878");
        using var generated = await client.PostAsync($"{Connections}/1/webhook-secret", new { csrf_token = await client.CsrfAsync() });
        var body = await Json(generated);
        var secret = body["webhook_secret"]!.GetValue<string>();
        Assert.Equal(("/api/v1/intake/webhook/deluno", "X-Webhook-Secret"), (body["webhook_url_path"]!.GetValue<string>(), body["header_name"]!.GetValue<string>()));
        using (var fetched = await client.GetAsync($"{Connections}/1"))
        {
            var text = await fetched.Content.ReadAsStringAsync();
            Assert.Contains("\"webhook_secret_is_set\":true", text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        var anonymous = new ApiTestClient(server);
        var handoff = new { eventType = "deluno.processor-handoff", mediaType = "movies", sourcePath = "/x/y.mkv" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/intake/webhook/deluno", handoff)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/intake/webhook/deluno", handoff, new Dictionary<string, string> { ["X-Webhook-Secret"] = "wrong" })).StatusCode);
        using var accepted = await anonymous.PostAsync("/api/v1/intake/webhook/deluno", handoff, new Dictionary<string, string> { ["X-Webhook-Secret"] = secret });
        Assert.Equal(HttpStatusCode.BadRequest, accepted.StatusCode);
        Assert.Contains("watched folder", await Detail(accepted), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/v1/intake/webhook/radarr", new { eventType = "Grab" })).StatusCode);
    }

    [Fact]
    public async Task A_connection_test_reports_unreachable_refused_and_removed_meanwhile()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client, baseUrl: "http://127.0.0.1:1");
        using (var unreachable = await client.PostAsync($"{Connections}/1/test", new { csrf_token = await client.CsrfAsync() }))
        {
            var body = await Json(unreachable);
            Assert.False(body["ok"]!.GetValue<bool>());
            Assert.Equal("Weir could not reach Deluno on 127.0.0.1 at http://127.0.0.1:1. Check the address is right, and that the app is running and reachable from this machine.", body["detail"]!.GetValue<string>());
            Assert.EndsWith("Z", body["checked_at"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using (var saved = await client.GetAsync($"{Connections}/1"))
        {
            var body = await Json(saved);
            Assert.False(body["last_test_ok"]!.GetValue<bool>());
            Assert.DoesNotContain("Z", body["last_test_at"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        manager.Json(HttpMethod.Get, "/api/integrations/external/health", "{}", HttpStatusCode.Forbidden);
        using (var refused = await client.PostAsync($"{Connections}/1/test", new { csrf_token = await client.CsrfAsync() }))
        {
            Assert.Equal("Weir reached Deluno on 127.0.0.1, but the API key was refused. Check the key and save it again.", (await Json(refused))["detail"]!.GetValue<string>());
        }

        manager.Route(HttpMethod.Get, "/api/integrations/external/health", async _ =>
        {
            await TestDatabase.ExecuteAsync(server, "DELETE FROM media_manager_connections;");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"ok"}""") };
        });
        using var removed = await client.PostAsync($"{Connections}/1/test", new { csrf_token = await client.CsrfAsync() });
        Assert.Equal((HttpStatusCode.NotFound, "That media manager connection was removed while its connection test was running."), (removed.StatusCode, await Detail(removed)));
    }

    /// <summary>
    /// #544 item 1: a manager answering 2xx with a body that is not JSON (a reverse proxy's HTML login page,
    /// most often) made the connection test 500 from an uncaught JSON-decode exception; it is now reported as a
    /// plain, unsuccessful test result.
    /// </summary>
    [Fact]
    public async Task A_connection_test_classifies_a_2xx_non_json_answer_instead_of_crashing()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await CreateAsync(client, "radarr", "http://192.0.2.20:7878");
        manager.Json(HttpMethod.Get, "/api/v3/system/status", "<html>this is a login page, not Radarr</html>");
        using var tested = await client.PostAsync($"{Connections}/1/test", new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var body = await Json(tested);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Equal(
            "Weir reached Radarr on 192.0.2.20 but did not get the answer it expected. Check the address points at the app itself, not a page inside it.",
            body["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Capabilities_report_what_each_credentialed_manager_manages()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        manager.Json(HttpMethod.Get, "/api/integrations/external/manifest", """{"libraries":[{"id":"lib-movies","name":"Movies","mediaType":"movies","path":"/media/movies"},{"id":"lib-tv","name":"TV","mediaType":"tv","path":"/media/tv"}]}""");
        await CreateAsync(client);
        await CreateAsync(client, baseUrl: "http://192.0.2.11:5099", apiKey: "");
        using var response = await client.GetAsync("/api/v1/media-managers/capabilities");
        Assert.Equal(
            """[{"connection_id":1,"kind":"deluno","name":"Deluno on 192.0.2.10","label":"Deluno on 192.0.2.10","media_scopes":["movie","tv"],"reports_import_queue":true,"reports_library_truth":false,"reachable":true,"library_roots":["/media/movies","/media/tv"],"summary":"Looks after Movies and TV episodes. Weir can ask it what is mid-import, but not which files it still keeps, so folder cleanup stays off unless another manager can answer that.","detail":null}]""",
            await response.Content.ReadAsStringAsync());
        Assert.Equal("deluno_secret_key", Assert.Single(manager.Requests).Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task The_intake_webhook_ignores_what_is_not_its_business_and_names_unknown_sources()
    {
        var (server, admin, _) = await StartAsync();
        await using var _server = server;
        await CreateAsync(admin, "sonarr", "http://192.0.2.30:8989");
        await CreateAsync(admin, "radarr", "http://192.0.2.20:7878");
        var client = new ApiTestClient(server);
        using (var grab = await client.PostAsync("/api/v1/intake/webhook/sonarr", new { eventType = "Grab", episodes = new[] { new { id = 1 } } }))
        {
            Assert.Equal("""{"status":"ignored","source":"sonarr"}""", await grab.Content.ReadAsStringAsync());
        }

        using (var imported = await client.PostAsync("/api/v1/intake/webhook/RADARR", new { eventType = "Download", movie = new { id = 3, title = "Film", year = 2010 }, movieFile = new { path = "/media/m/f.mkv" } }))
        {
            Assert.Equal("""{"status":"ignored","source":"radarr","event":"imported"}""", await imported.Content.ReadAsStringAsync());
        }

        using (var unknown = await client.PostAsync("/api/v1/intake/webhook/plex", new { }))
        {
            Assert.Equal((HttpStatusCode.NotFound, "Unknown media manager source 'plex'. Known sources: deluno, native, radarr, sonarr. Use 'native' for a manager without a dialect of its own."), (unknown.StatusCode, await Detail(unknown)));
        }

        using var notADict = await client.SendAsync(HttpMethod.Post, "/api/v1/intake/webhook/native", content: TestDatabase.RawJson("[1]"));
        Assert.Equal("""{"detail":[{"type":"dict_type","loc":["body"],"msg":"Input should be a valid dictionary","input":[1]}]}""", await notADict.Content.ReadAsStringAsync());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task A_configured_instance_secret_is_required_and_only_the_current_name_configures_it()
    {
        await using (var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret")]))
        {
            var client = new ApiTestClient(server);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/intake/webhook/radarr", new { eventType = "Grab" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/intake/webhook/radarr", new { eventType = "Grab" }, new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" })).StatusCode);
        }

        // WEIR_SUBBER_WEBHOOK_SECRET is a retired name that configures nothing, so an existing connection
        // behaves exactly as it would with no instance secret at all: it keeps accepting an unsigned write.
        {
            var (server, admin, _) = await StartAsync(("WEIR_SUBBER_WEBHOOK_SECRET", "s3cret"));
            await using var _server = server;
            await CreateAsync(admin, "radarr", "http://192.0.2.20:7878");
            var client = new ApiTestClient(server);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/intake/webhook/radarr", new { eventType = "Grab" })).StatusCode);
        }
    }

    [Fact]
    public async Task Intake_capabilities_report_a_pre_release_version_exactly()
    {
        var (server, _, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"), ("WEIR_VERSION", "1.0.0-rc.1"));
        await using var _server = server;
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };

        using var response = await new ApiTestClient(server).GetAsync("/api/v1/intake/capabilities", secret);

        Assert.Equal("1.0.0-rc.1", (await Json(response))["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_hand_off_is_queued_answered_for_and_cancelled_over_http()
    {
        var watched = Path.Join(Path.GetTempPath(), "weir-handoff-" + Guid.NewGuid().ToString("N"));
        var (server, _, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
        await using var _server = server;
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));
        var manager = new ApiTestClient(server);
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };

        using (var noSecret = await manager.GetAsync("/api/v1/intake/capabilities"))
        {
            Assert.Equal((HttpStatusCode.Unauthorized, "Invalid or missing X-Webhook-Secret header."), (noSecret.StatusCode, await Detail(noSecret)));
        }

        Assert.Equal(
            $$"""{"capabilities":["handoff-status","handoff-cancel","handoff-outcome","handoff-outcome-codes","library-folders"],"machine_name":"{{Environment.MachineName}}","product":"Weir","version":"{{WeirVersion.BuildVersion}}"}""",
            await (await manager.GetAsync("/api/v1/intake/capabilities", secret)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/v1/intake/handoffs/deluno/h1", secret)).StatusCode);
        Assert.Equal("Unknown media manager source 'plex'.", await Detail(await manager.GetAsync("/api/v1/intake/handoffs/plex/h1", secret)));

        var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h1", libraryId = "lib-1", mediaType = "movies", sourcePath = Path.Join(watched, "Film", "film.mkv"), callbackPath = "/api/integrations/processors/events" };
        using (var queued = await manager.PostAsync("/api/v1/intake/webhook/deluno", handoff, secret))
        {
            Assert.Equal("""{"status":"ok","source":"deluno","event":"handoff","media_scope":"movie","enqueued":"processing.file.remux_pass.v1"}""", await queued.Content.ReadAsStringAsync());
        }

        using (var status = await manager.GetAsync("/api/v1/intake/handoffs/deluno/h1", secret))
        {
            var body = await Json(status);
            Assert.Equal(("queued", 1), (body["state"]!.GetValue<string>(), body["queuePosition"]!.GetValue<int>()));
            Assert.EndsWith("Z", body["lastChangedUtc"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using (var cancelled = await manager.SendAsync(HttpMethod.Delete, "/api/v1/intake/handoffs/deluno/h1", headers: secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
            Assert.Null(cancelled.Content.Headers.ContentType);
        }

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_cancelled' AND title = 'Deluno cancelled its hand-off of film.mkv'"));
        Assert.Equal("cancelled", (await Json(await manager.GetAsync("/api/v1/intake/handoffs/deluno/h1", secret)))["state"]!.GetValue<string>());
        using var again = await manager.SendAsync(HttpMethod.Delete, "/api/v1/intake/handoffs/deluno/h1", headers: secret);
        Assert.Equal((HttpStatusCode.Conflict, "This hand-off is cancelled, so Weir did not cancel it."), (again.StatusCode, await Detail(again)));
    }

    /// <summary>
    /// <c>GET /intake/library-folders</c> (#768): every enabled library's watched, work and output folders, so a
    /// manager reads them instead of a person retyping them; a disabled library is left out, and the route needs
    /// the same webhook secret as the rest of the intake surface.
    /// </summary>
    [Fact]
    public async Task Library_folders_are_published_to_an_authenticated_manager()
    {
        var (server, admin, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
        await using var _server = server;
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };

        // A fresh database seeds a Movies and a TV library; give the movie one folders to publish and disable the
        // TV one, so the published list is exactly one library.
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE libraries SET watched_folder = $w, work_folder = $k, output_folder = $o WHERE media_type = 'movie'",
            ("$w", "/media/movies/watched"),
            ("$k", "/media/movies/work"),
            ("$o", "/media/movies/output"));
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET enabled = 0 WHERE media_type = 'tv'");
        var movieId = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries WHERE media_type = 'movie'");

        using (var unauthenticated = await admin.GetAsync("/api/v1/intake/library-folders"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }

        using var authenticated = await admin.GetAsync("/api/v1/intake/library-folders", secret);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        var body = (await Json(authenticated))!;
        var library = Assert.Single(body["libraries"]!.AsArray())!;
        Assert.Equal(movieId, library["id"]!.GetValue<long>());
        Assert.Equal("Movies", library["name"]!.GetValue<string>());
        Assert.Equal("movie", library["media_type"]!.GetValue<string>());
        Assert.Equal("/media/movies/watched", library["watched_folder"]!.GetValue<string>());
        Assert.Equal("/media/movies/work", library["work_folder"]!.GetValue<string>());
        Assert.Equal("/media/movies/output", library["output_folder"]!.GetValue<string>());
        Assert.Equal(LibraryIntake.DefaultMinFileSizeMb * 1024 * 1024, library["minimum_file_size_bytes"]!.GetValue<long>());
    }

    /// <summary>A workflow's minimum file size is published in bytes, and as null once the workflow has no minimum.</summary>
    [Fact]
    public async Task Library_folders_carry_each_workflows_minimum_file_size()
    {
        var (server, admin, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
        await using var _server = server;
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET min_file_size_mb = 120 WHERE media_type = 'movie'");
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET min_file_size_mb = 0 WHERE media_type = 'tv'");

        using var response = await admin.GetAsync("/api/v1/intake/library-folders", secret);
        var minimums = (await Json(response))!["libraries"]!.AsArray().ToDictionary(
            library => library!["media_type"]!.GetValue<string>(),
            library => library!["minimum_file_size_bytes"]?.GetValue<long>());

        Assert.Equal(120L * 1024 * 1024, minimums["movie"]);
        Assert.Null(minimums["tv"]);
    }

    /// <summary>
    /// A workflow set up from a media manager publishes that manager's own library id, and says whether the manager still owns
    /// its folders: Deluno does, a manager that cannot report folders does not, and a workflow no longer set up from a manager
    /// publishes neither.
    /// </summary>
    [Fact]
    public async Task Library_folders_carry_the_managers_library_key_and_whether_it_owns_the_folders()
    {
        var (server, admin, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
        await using var _server = server;
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };
        var deluno = (await CreateAsync(admin))["id"]!.GetValue<long>();
        var sonarr = (await CreateAsync(admin, "sonarr", "http://nas:8989"))["id"]!.GetValue<long>();
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE libraries SET discovered_from_connection_id = CASE media_type WHEN 'movie' THEN $deluno ELSE $sonarr END, " +
            "discovered_library_key = CASE media_type WHEN 'movie' THEN 'lib-movies' ELSE '7' END",
            ("$deluno", deluno),
            ("$sonarr", sonarr));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO library_manager_links (library_id, connection_id) SELECT id, discovered_from_connection_id FROM libraries");

        async Task<Dictionary<string, (string? Key, bool FromManager)>> PublishedAsync()
        {
            using var response = await admin.GetAsync("/api/v1/intake/library-folders", secret);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response))!["libraries"]!.AsArray().ToDictionary(
                library => library!["media_type"]!.GetValue<string>(),
                library => (library!["manager_library_key"]?.GetValue<string>(), library["folders_from_manager"]!.GetValue<bool>()));
        }

        var published = await PublishedAsync();
        Assert.Equal(("lib-movies", true), published["movie"]);
        Assert.Equal(("7", false), published["tv"]);

        await TestDatabase.ExecuteAsync(
            server, "UPDATE libraries SET discovered_from_connection_id = NULL, discovered_library_key = NULL WHERE media_type = 'movie'");

        Assert.Equal(((string?)null, false), (await PublishedAsync())["movie"]);
    }

    /// <summary>
    /// A library's <c>work_folder</c> column is blank whenever it uses Weir's own default (most libraries never set
    /// one), so the published folder must be the effective default, the same one <c>ProcessingLibraryFolders</c> and
    /// the folder-chain check resolve — not the blank column value.
    /// </summary>
    [Fact]
    public async Task A_librarys_default_work_folder_is_published_resolved_not_blank()
    {
        var (server, admin, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
        await using var _server = server;
        var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE libraries SET watched_folder = $w, work_folder = '', output_folder = $o WHERE media_type = 'movie'",
            ("$w", "/media/movies/watched"),
            ("$o", "/media/movies/output"));
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET enabled = 0 WHERE media_type = 'tv'");
        var weirHome = server.Services.GetRequiredService<WeirOptions>().WeirHome;

        using var response = await admin.GetAsync("/api/v1/intake/library-folders", secret);

        var library = Assert.Single((await Json(response))!["libraries"]!.AsArray())!;
        Assert.Equal(ProcessingLibraryFolders.DefaultMovieWorkFolder(weirHome), library["work_folder"]!.GetValue<string>());
        Assert.NotEqual(string.Empty, library["work_folder"]!.GetValue<string>());
    }

    /// <summary>
    /// The secret that created a hand-off (or the instance-wide fallback a secret-less connection accepts) is the
    /// only one that can ask about it or cancel it; another same-kind connection's own secret is refused.
    /// </summary>
    [Fact]
    public async Task A_hand_offs_secret_scopes_it_to_the_connection_that_received_it()
    {
        var watched = Path.Join(Path.GetTempPath(), "weir-handoff-" + Guid.NewGuid().ToString("N"));
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));

        var connectionA = await CreateAsync(client, "native", baseUrl: "", apiKey: "");
        var connectionB = await CreateAsync(client, "native", baseUrl: "", apiKey: "");
        var secretA = await RotateWebhookSecretAsync(client, connectionA["id"]!.GetValue<int>());
        var secretB = await RotateWebhookSecretAsync(client, connectionB["id"]!.GetValue<int>());
        var headersA = new Dictionary<string, string> { ["X-Webhook-Secret"] = secretA };
        var headersB = new Dictionary<string, string> { ["X-Webhook-Secret"] = secretB };

        var manager = new ApiTestClient(server);
        var handoff = new { @event = "handoff", mediaScope = "movie", filePath = Path.Join(watched, "Film", "film.mkv"), handoffId = "h1", callbackPath = "/callback" };
        using (var queued = await manager.PostAsync("/api/v1/intake/webhook/native", handoff, headersA))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        using (var wrongSecret = await manager.GetAsync("/api/v1/intake/handoffs/native/h1", headersB))
        {
            Assert.Equal((HttpStatusCode.Unauthorized, "Invalid or missing X-Webhook-Secret header."), (wrongSecret.StatusCode, await Detail(wrongSecret)));
        }

        using var ownSecret = await manager.GetAsync("/api/v1/intake/handoffs/native/h1", headersA);
        Assert.Equal(HttpStatusCode.OK, ownSecret.StatusCode);
    }

    private static async Task<string> RotateWebhookSecretAsync(ApiTestClient client, int connectionId)
    {
        using var response = await client.PostAsync($"{Connections}/{connectionId}/webhook-secret", new { csrf_token = await client.CsrfAsync() });
        return (await Json(response))["webhook_secret"]!.GetValue<string>();
    }

    [Fact]
    public async Task Cancelling_a_hand_offs_job_on_the_jobs_screen_tells_the_manager_it_is_cancelled()
    {
        var watched = Path.Join(Path.GetTempPath(), "weir-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(watched, "Film", "film.mkv"), "12345");
        try
        {
            var (server, client, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
            await using var _server = server;
            await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));
            var manager = new ApiTestClient(server);
            var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };
            var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h1", libraryId = "lib-1", mediaType = "movies", sourcePath = Path.Join(watched, "Film", "film.mkv"), callbackPath = "/api/integrations/processors/events" };
            using (var queued = await manager.PostAsync("/api/v1/intake/webhook/deluno", handoff, secret))
            {
                Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
            }

            var jobId = await TestDatabase.ScalarAsync(server, "SELECT id FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
            using (var cancel = await client.PostAsync($"/api/v1/processing/jobs/{jobId}/cancel-pending", new { csrf_token = await client.CsrfAsync() }))
            {
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }

            var status = await Json(await manager.GetAsync("/api/v1/intake/handoffs/deluno/h1", secret));
            Assert.Equal(
                ("cancelled", "Someone cancelled this hand-off in Weir before Weir started on it."),
                (status["state"]!.GetValue<string>(), status["message"]!.GetValue<string>()));
            Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM files WHERE relative_path = 'Film/film.mkv' AND status = 'cancelled'"));
            Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_cancelled' AND title = 'The hand-off of film.mkv from Deluno was cancelled in Weir'"));
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }

    [Fact]
    public async Task A_release_folder_hand_off_completes_though_a_sample_beside_it_was_left_waiting_by_a_paused_scan()
    {
        var watched = Path.Join(Path.GetTempPath(), "weir-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(watched, "Film", "film.mkv"), "12345");
        await File.WriteAllTextAsync(Path.Join(watched, "Film", "film-sample.mkv"), "1");
        try
        {
            var (server, _, _) = await StartAsync(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "s3cret"));
            await using var _server = server;
            await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));
            var manager = new ApiTestClient(server);
            var secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" };
            var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h1", libraryId = "lib-1", mediaType = "movies", sourcePath = Path.Join(watched, "Film"), callbackPath = "/api/integrations/processors/events" };
            using (var queued = await manager.PostAsync("/api/v1/intake/webhook/deluno", handoff, secret))
            {
                Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
            }

            // A watched-folder scan that ran while processing was paused recorded the sample as waiting; later the main file was processed.
            await TestDatabase.ExecuteAsync(
                server,
                "INSERT INTO files (library_id, relative_path, status, status_reason) " +
                "SELECT id, 'Film/film-sample.mkv', 'out_of_schedule', 'Processing is paused.' FROM libraries WHERE media_type = 'movie'");
            await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed'; UPDATE files SET status = 'processed' WHERE relative_path = 'Film/film.mkv'");

            var status = await Json(await manager.GetAsync("/api/v1/intake/handoffs/deluno/h1", secret));

            Assert.Equal("completed", status["state"]!.GetValue<string>());
            Assert.Null(status["scheduledFor"]);
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }

    [Fact]
    public async Task Hand_off_routes_without_any_secret_say_what_to_set()
    {
        var (server, _, _) = await StartAsync();
        await using var _server = server;
        using var response = await new ApiTestClient(server).SendAsync(HttpMethod.Delete, "/api/v1/intake/handoffs/deluno/h1");
        Assert.Equal((HttpStatusCode.Forbidden, "Set a webhook secret in Weir so a media manager can ask about hand-offs."), (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task A_reconciliation_repair_needs_the_origin_check_and_a_csrf_token()
    {
        var (server, client, _) = await StartAsync(("WEIR_TRUSTED_BROWSER_ORIGINS", "http://weir.local"));
        await using var _server = server;
        var work = Path.Join(server.Home, "work");
        Directory.CreateDirectory(work);
        var artifact = Path.Join(work, ".movie.mkv.partial");
        await File.WriteAllTextAsync(artifact, "partial");
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET work_folder = $w WHERE media_type = 'movie'", ("$w", work));
        var origin = new Dictionary<string, string> { ["Origin"] = "http://weir.local", ["X-Requested-With"] = "XMLHttpRequest" };

        using (var report = await client.GetAsync("/api/v1/system/reconciliation"))
        {
            var issue = (await Json(report))["issues"]!.AsArray().Single(i => i!["kind"]!.GetValue<string>() == "partial_temp_artifact")!;
            Assert.Equal((artifact, true), (issue["path"]!.GetValue<string>(), issue["requires_confirmation"]!.GetValue<bool>()));
        }

        using (var noToken = await client.PostAsync("/api/v1/system/reconciliation/repair", new { action = "remove_processing_temp_artifact", path = artifact, confirm = true }, origin))
        {
            Assert.Equal((HttpStatusCode.BadRequest, "Invalid or expired CSRF token."), (noToken.StatusCode, await Detail(noToken)));
        }

        using (var badOrigin = await client.PostAsync("/api/v1/system/reconciliation/repair", new { action = "remove_processing_temp_artifact", path = artifact, confirm = true, csrf_token = await client.CsrfAsync() }, new Dictionary<string, string> { ["Origin"] = "http://evil.example", ["X-Requested-With"] = "XMLHttpRequest" }))
        {
            Assert.Equal((HttpStatusCode.Forbidden, "Origin not allowed."), (badOrigin.StatusCode, await Detail(badOrigin)));
        }

        Assert.True(File.Exists(artifact));
        using (var unconfirmed = await client.PostAsync("/api/v1/system/reconciliation/repair", new { action = "remove_processing_temp_artifact", path = artifact, confirm = false, csrf_token = await client.CsrfAsync() }, origin))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            Assert.Contains("confirm=true", await Detail(unconfirmed), StringComparison.Ordinal);
        }

        using var applied = await client.PostAsync("/api/v1/system/reconciliation/repair", new { action = "remove_processing_temp_artifact", path = artifact, confirm = true, csrf_token = await client.CsrfAsync() }, origin);
        Assert.Equal("""{"applied":true,"message":"Removed the temp artifact."}""", await applied.Content.ReadAsStringAsync());
        Assert.False(File.Exists(artifact));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'system.reconciliation.repair' AND detail = 'remove_processing_temp_artifact: Removed the temp artifact.'"));
    }

    [Fact]
    public async Task Head_on_the_post_only_webhook_is_method_not_allowed()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_WEB_DIST", "")]);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/api/v1/intake/webhook/deluno");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await server.Client.SendAsync(request)).StatusCode);
    }
}
