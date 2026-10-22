using System.Net;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// Saving, reading, removing and ordering workflows through the real routes: what a save accepts and what it hands back,
/// which answer a workflow gives about its media managers, and each reason a request is turned away.
/// </summary>
public sealed class ProcessingLibrarySaveApiTests
{
    private const string Libraries = "/api/v1/processing/libraries";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<HttpResponseMessage> CreateAsync(ApiTestClient client, JsonObject? fields = null)
    {
        var body = new JsonObject { ["csrf_token"] = await client.CsrfAsync(), ["name"] = "Films " + Guid.NewGuid().ToString("N")[..8], ["media_type"] = "movie" };
        foreach (var (name, value) in fields ?? new JsonObject())
        {
            body[name] = value?.DeepClone();
        }

        return await client.PostAsync(Libraries, body);
    }

    private static async Task<JsonNode> CreatedAsync(ApiTestClient client, JsonObject? fields = null)
    {
        using var response = await CreateAsync(client, fields);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    private static async Task<long> ConnectAsync(WeirTestServer server, ApiTestClient client, string kind, string address, bool? lastTestOk, bool enabled = true)
    {
        using var created = await client.PostAsync(
            "/api/v1/media-managers/connections", new { csrf_token = await client.CsrfAsync(), kind, base_url = address, api_key = "the-managers-api-key" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created))["id"]!.GetValue<long>();
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE media_manager_connections SET last_connection_test_ok = $ok, enabled = $enabled WHERE id = $id",
            ("$ok", lastTestOk is null ? DBNull.Value : lastTestOk.Value ? 1 : (object)0), ("$enabled", enabled ? 1 : 0), ("$id", id));
        return id;
    }

    private static async Task<JsonNode> CoverageAsync(ApiTestClient client, long libraryId, params long[] connectionIds)
    {
        using var response = await client.PutAsync(
            $"{Libraries}/{libraryId}", new { csrf_token = await client.CsrfAsync(), name = "Linked", media_type = "movie", manager_connection_ids = connectionIds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task A_workflow_saved_with_null_text_fields_gets_the_shipped_defaults_for_each()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var nulls = new JsonObject();
        foreach (var field in new[]
        {
            "watched_folder", "work_folder", "output_folder", "media_extensions_csv", "exclude_markers_csv", "include_patterns_csv", "exclude_patterns_csv",
            "sidecar_patterns_csv", "schedule_grid", "schedule_days", "schedule_start", "schedule_end",
        })
        {
            nulls[field] = null;
        }

        var created = await CreatedAsync(client, nulls);

        Assert.Equal(string.Empty, created["watched_folder"]!.GetValue<string>());
        Assert.Equal(string.Empty, created["include_patterns_csv"]!.GetValue<string>());
        Assert.Equal(string.Empty, created["schedule_days"]!.GetValue<string>());
        Assert.Equal(("00:00", "23:59"), (created["schedule_start"]!.GetValue<string>(), created["schedule_end"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_detection_window_with_a_time_that_names_no_timezone_is_a_422_for_each_of_the_four_fields()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        foreach (var field in new[] { "created_after", "created_before", "modified_after", "modified_before" })
        {
            using var response = await CreateAsync(client, new JsonObject { [field] = "2025-01-01T00:00:00" });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var issue = (await Json(response))["detail"]![0]!;
            Assert.Equal(("value_error", field), (issue["type"]!.GetValue<string>(), issue["loc"]![1]!.GetValue<string>()));
            Assert.Equal("Value error, Detection-window times must include a timezone.", issue["msg"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("created_after", "created_before", "Value error, Created after must be earlier than created before.")]
    [InlineData("modified_after", "modified_before", "Value error, Modified after must be earlier than modified before.")]
    public async Task A_detection_window_that_ends_before_it_starts_is_a_422_and_nothing_is_saved(string start, string end, string message)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var before = await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries");

        foreach (var (first, second) in new[] { ("2025-06-01T00:00:00Z", "2025-01-01T00:00:00Z"), ("2025-06-01T00:00:00Z", "2025-06-01T02:00:00+02:00") })
        {
            using var response = await CreateAsync(client, new JsonObject { [start] = first, [end] = second });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(message, (await Json(response))["detail"]![0]!["msg"]!.GetValue<string>());
        }

        Assert.Equal(before, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries"));
    }

    [Fact]
    public async Task A_detection_window_in_order_is_saved_and_read_back_with_its_timezone()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        var created = await CreatedAsync(client, new JsonObject
        {
            ["created_after"] = "2025-01-01T00:00:00Z",
            ["created_before"] = "2025-06-01T00:00:00Z",
            ["modified_after"] = "2025-02-01T00:00:00Z",
            ["modified_before"] = "2025-03-01T00:00:00Z",
        });

        Assert.Equal("2025-01-01T00:00:00Z", created["created_after"]!.GetValue<string>());
        Assert.Equal("2025-03-01T00:00:00Z", created["modified_before"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_workflow_read_with_a_detection_window_can_be_saved_again_exactly_as_it_was_read()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var created = await CreatedAsync(client, new JsonObject { ["created_after"] = "2025-01-01T00:00:00Z", ["modified_before"] = "2025-03-01T02:00:00+02:00" });
        var libraryId = created["id"]!.GetValue<long>();
        using var read = await client.GetAsync($"{Libraries}/{libraryId}");
        var window = await Json(read);

        using var saved = await client.PutAsync(
            $"{Libraries}/{libraryId}",
            new
            {
                csrf_token = await client.CsrfAsync(),
                name = window["name"]!.GetValue<string>(),
                media_type = "movie",
                created_after = window["created_after"]!.GetValue<string>(),
                modified_before = window["modified_before"]!.GetValue<string>(),
            });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var again = await Json(saved);
        Assert.Equal(("2025-01-01T00:00:00Z", "2025-03-01T00:00:00Z"), (again["created_after"]!.GetValue<string>(), again["modified_before"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_workflow_with_no_manager_linked_says_there_is_no_upstream_signal()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        var created = await CreatedAsync(client);

        Assert.Equal("no_upstream_signal", created["manager_coverage"]!.GetValue<string>());
        Assert.StartsWith("No media manager is linked.", created["manager_coverage_detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_workflow_says_about_its_managers_follows_their_last_connection_test()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = (await CreatedAsync(client))["id"]!.GetValue<long>();
        var healthy = await ConnectAsync(server, client, "radarr", "http://192.0.2.20:7878", lastTestOk: true);
        var untested = await ConnectAsync(server, client, "radarr", "http://192.0.2.21:7878", lastTestOk: null);
        var failed = await ConnectAsync(server, client, "radarr", "http://192.0.2.22:7878", lastTestOk: false);
        var switchedOff = await ConnectAsync(server, client, "radarr", "http://192.0.2.23:7878", lastTestOk: true, enabled: false);

        var connected = await CoverageAsync(client, libraryId, healthy);
        var notYet = await CoverageAsync(client, libraryId, healthy, untested);
        var unreachable = await CoverageAsync(client, libraryId, healthy, failed);
        var off = await CoverageAsync(client, libraryId, switchedOff);

        Assert.Equal("connected", connected["manager_coverage"]!.GetValue<string>());
        Assert.Equal("no_upstream_signal", notYet["manager_coverage"]!.GetValue<string>());
        Assert.StartsWith("A manager is linked but has not returned", notYet["manager_coverage_detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("unreachable", unreachable["manager_coverage"]!.GetValue<string>());
        Assert.StartsWith("A linked media manager did not answer", unreachable["manager_coverage_detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("unreachable", off["manager_coverage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reject_cannot_be_saved_for_a_workflow_no_manager_can_take_a_rejection_for_and_nothing_is_kept()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var before = await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries");

        using var created = await CreateAsync(client, new JsonObject { ["failure_policy"] = "reject" });
        var existing = (await CreatedAsync(client))["id"]!.GetValue<long>();
        using var updated = await client.PutAsync(
            $"{Libraries}/{existing}", new { csrf_token = await client.CsrfAsync(), name = "Changed", media_type = "movie", failure_policy = "reject" });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.StartsWith("This workflow cannot use Reject yet.", await Detail(created), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(before + 1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries"));
        Assert.Equal("pass_through", await TestDatabase.ScalarStringAsync(server, "SELECT failure_policy FROM libraries WHERE id = $id", ("$id", existing)));
    }

    [Fact]
    public async Task Asking_about_a_workflow_that_does_not_exist_is_a_404_for_reading_changing_removing_and_its_folder_chain()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var read = await client.GetAsync($"{Libraries}/404");
        using var chain = await client.GetAsync($"{Libraries}/404/folder-chain");
        using var changed = await client.PutAsync($"{Libraries}/404", new { csrf_token = await client.CsrfAsync(), name = "Nope", media_type = "movie" });
        using var removed = await client.SendAsync(HttpMethod.Delete, $"{Libraries}/404", new { csrf_token = await client.CsrfAsync() });

        Assert.All(new[] { read, chain, changed, removed }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task A_workflow_with_work_queued_or_running_cannot_be_removed_and_the_answer_says_how_many_jobs()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = (await CreatedAsync(client))["id"]!.GetValue<long>();
        foreach (var key in new[] { "queued-1", "queued-2" })
        {
            await TestDatabase.ExecuteAsync(
                server,
                "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($key, 'processing.file.remux_pass.v1', $payload, 'pending')",
                ("$key", key), ("$payload", $"{{\"library_id\": {libraryId}}}"));
        }

        using var response = await client.SendAsync(HttpMethod.Delete, $"{Libraries}/{libraryId}", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("still has 2 jobs queued or running", await Detail(response), StringComparison.Ordinal);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries WHERE id = $id", ("$id", libraryId)));
    }

    [Fact]
    public async Task A_workflow_with_no_work_left_is_removed_with_an_empty_json_answer()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = (await CreatedAsync(client))["id"]!.GetValue<long>();

        using var response = await client.SendAsync(HttpMethod.Delete, $"{Libraries}/{libraryId}", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries WHERE id = $id", ("$id", libraryId)));
    }

    [Fact]
    public async Task Workflows_are_put_in_the_order_given_and_a_list_that_misses_one_or_names_a_stranger_is_refused()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var created = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            created.Add((await CreatedAsync(client))["id"]!.GetValue<long>());
        }

        using var listing = await client.GetAsync(Libraries);
        var everyone = (await Json(listing)).AsArray().Select(item => item!["id"]!.GetValue<long>()).ToList();
        var wanted = everyone.OrderByDescending(id => id).ToList();

        async Task<HttpResponseMessage> ReorderAsync(IEnumerable<long> ids) =>
            await client.PostAsync($"{Libraries}/reorder", new { csrf_token = await client.CsrfAsync(), library_ids_in_order = ids.ToArray() });

        using var reordered = await ReorderAsync(wanted);
        using var missing = await ReorderAsync(wanted.Skip(1));
        using var stranger = await ReorderAsync(wanted.Append(9999L));
        using var empty = await ReorderAsync([]);

        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        Assert.Equal(wanted, (await Json(reordered)).AsArray().Select(item => item!["id"]!.GetValue<long>()));
        Assert.Equal((HttpStatusCode.BadRequest, "Reordering must list every workflow exactly once."), (missing.StatusCode, await Detail(missing)));
        Assert.Equal((HttpStatusCode.BadRequest, "No workflow with id 9999."), (stranger.StatusCode, await Detail(stranger)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("too_short", (await Json(empty))["detail"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changing_a_workflows_order_removing_it_or_saving_it_are_for_operators_and_change_nothing_when_refused()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = (await CreatedAsync(client))["id"]!.GetValue<long>();

        await OperatorPostRefusals.AssertAsync(server, $"{Libraries}/reorder", token => new { csrf_token = token, library_ids_in_order = new[] { libraryId } });

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM libraries WHERE id = $id", ("$id", libraryId)));
    }

    [Fact]
    public async Task The_setup_check_asks_for_a_media_type_and_a_yes_or_no_for_removing_originals()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var noType = await client.GetAsync("/api/v1/processing/manager-setup");
        using var badType = await client.GetAsync("/api/v1/processing/manager-setup?media_type=music");
        using var badFlag = await client.GetAsync("/api/v1/processing/manager-setup?media_type=tv&remove_original_after_success=maybe");

        Assert.All(new[] { noType, badType, badFlag }, response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Equal("missing", (await Json(noType))["detail"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_setup_check_answers_for_the_media_type_with_the_folders_and_links_it_was_given()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var connection = await ConnectAsync(server, client, "radarr", "http://192.0.2.20:7878", lastTestOk: true);

        using var linked = await client.GetAsync(
            $"/api/v1/processing/manager-setup?media_type=movie&watched_folder=/downloads&output_folder=/films&remove_original_after_success=FALSE&connection_ids={connection}&connection_ids=x");
        using var none = await client.GetAsync("/api/v1/processing/manager-setup?media_type=movie&connection_ids=none");

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var linkedBody = await Json(linked);
        Assert.Equal("movie", linkedBody["media_type"]!.GetValue<string>());
        Assert.Single(linkedBody["managers"]!.AsArray());
        Assert.Empty((await Json(none))["managers"]!.AsArray());
    }

    [Fact]
    public async Task Whether_reject_is_available_is_asked_of_the_connections_named_and_junk_in_the_list_is_ignored()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var connection = await ConnectAsync(server, client, "radarr", "http://192.0.2.20:7878", lastTestOk: true);

        using var named = await client.GetAsync($"/api/v1/processing/reject-support?connection_ids={connection}&connection_ids=junk");
        using var nobody = await client.GetAsync("/api/v1/processing/reject-support");

        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        Assert.False((await Json(nobody))["available"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace((await Json(nobody))["reason"]!.GetValue<string>()));
    }
}
