using System.Net;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// Removing a media manager connection is refused while a workflow still comes from it, while a hand-off from it is still in
/// progress, or while Weir owes it a report; the person unlinks or removes the workflows as separate steps, and no folder on disk
/// is touched by any of it.
/// </summary>
public sealed class ConnectionRemovalApiTests : IAsyncLifetime
{
    private const string Connections = "/api/v1/media-managers/connections";
    private const string Libraries = "/api/v1/processing/libraries";
    private const string WebhookSecret = "s3cret";
    private const string Original = "the original download, still being seeded";

    private static readonly Dictionary<string, string> SecretHeader = new() { ["X-Webhook-Secret"] = WebhookSecret };

    private readonly string _watched = Path.Join(Path.GetTempPath(), "weir-removal-" + Guid.NewGuid().ToString("N"));
    private WeirTestServer _server = null!;
    private ApiTestClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Join(_watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(_watched, "Film", "film.mkv"), Original);
        _server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", WebhookSecret)]);
        await TestDatabase.SeedAdminAsync(_server);
        _client = new ApiTestClient(_server);
        using var login = await _client.PostAsync(
            "/api/v1/auth/login", new { username = "alice", password = AdminPassword, csrf_token = await _client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await TestDatabase.ExecuteAsync(_server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", _watched));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_watched, recursive: true);
    }

    private async Task<long> AddDelunoAsync(string address = "http://192.0.2.10:5099")
    {
        using var created = await _client.PostAsync(
            Connections, new { csrf_token = await _client.CsrfAsync(), kind = "deluno", base_url = address, api_key = "deluno_secret_key" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created))["id"]!.GetValue<long>();
    }

    private Task LinkAsync(string mediaType, long connectionId) =>
        TestDatabase.ExecuteAsync(
            _server,
            "INSERT INTO library_manager_links (library_id, connection_id) SELECT id, $c FROM libraries WHERE media_type = $t",
            ("$c", connectionId),
            ("$t", mediaType));

    private async Task<HttpResponseMessage> RemoveAsync(long connectionId) =>
        await _client.SendAsync(HttpMethod.Delete, $"{Connections}/{connectionId}", new { csrf_token = await _client.CsrfAsync() });

    private async Task<HttpResponseMessage> UnlinkWorkflowsAsync(long connectionId) =>
        await _client.PostAsync($"{Connections}/{connectionId}/unlink-workflows", new { csrf_token = await _client.CsrfAsync() });

    private async Task HandOffAsync(string handoffId, string folder = "Film")
    {
        var handoff = new
        {
            eventType = "deluno.processor-handoff",
            handoffId,
            libraryId = "lib-1",
            mediaType = "movies",
            sourcePath = Path.Join(_watched, folder, "film.mkv"),
            callbackPath = "/api/integrations/processors/events",
        };
        using var queued = await new ApiTestClient(_server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader);
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
    }

    private async Task CancelQueuedPassAsync()
    {
        var jobId = await TestDatabase.ScalarAsync(_server, "SELECT id FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        using var cancel = await _client.PostAsync($"/api/v1/processing/jobs/{jobId}/cancel-pending", new { csrf_token = await _client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    private void AssertOriginalUntouched() =>
        Assert.Equal(Original, File.ReadAllText(Path.Join(_watched, "Film", "film.mkv")));

    private Task<long> CountAsync(string sql) => TestDatabase.ScalarAsync(_server, sql);

    [Fact]
    public async Task A_connection_that_feeds_workflows_is_not_removed_and_the_answer_names_them()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);
        await LinkAsync("tv", id);

        using var refused = await RemoveAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await Json(refused);
        Assert.Equal("connection_in_use", body["code"]!.GetValue<string>());
        Assert.Equal(
            "Movies and TV still come from Deluno on 192.0.2.10. Nothing was removed. " +
            "Before removing it, unlink or remove those workflows.",
            body["detail"]!.GetValue<string>());
        Assert.Equal(
            """[{"id":1,"name":"Movies"},{"id":2,"name":"TV"}]""",
            body["workflows"]!.ToJsonString());
        Assert.Equal((0, 0), (body["handoffs_in_progress"]!.GetValue<int>(), body["reports"]!.AsArray().Count));
        Assert.Equal((1L, 2L, 2L), (await CountAsync("SELECT count(*) FROM media_manager_connections"), await CountAsync("SELECT count(*) FROM library_manager_links"), await CountAsync("SELECT count(*) FROM libraries WHERE enabled = 1")));
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task One_workflow_is_named_in_the_singular()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);

        using var refused = await RemoveAsync(id);

        Assert.StartsWith("Movies still comes from Deluno on 192.0.2.10. ", await Detail(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_linked_to_another_connection_does_not_hold_this_one()
    {
        var feeding = await AddDelunoAsync();
        var idle = await AddDelunoAsync("http://192.0.2.11:5099");
        await LinkAsync("movie", feeding);

        using var removed = await RemoveAsync(idle);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal((1L, 1L), (await CountAsync("SELECT count(*) FROM media_manager_connections"), await CountAsync("SELECT count(*) FROM library_manager_links")));
    }

    [Fact]
    public async Task A_connection_nothing_depends_on_is_removed()
    {
        var id = await AddDelunoAsync();

        using var removed = await RemoveAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM media_manager_connections"));
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task A_hand_off_still_in_progress_holds_the_connection_until_it_is_cancelled()
    {
        var id = await AddDelunoAsync();
        await HandOffAsync("h1");

        using (var refused = await RemoveAsync(id))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await Json(refused);
            Assert.Equal("connection_in_use", body["code"]!.GetValue<string>());
            Assert.Equal(
                "Weir is still working on 1 hand-off from Deluno on 192.0.2.10. Nothing was removed. " +
                "Before removing it, let the hand-off finish or cancel it.",
                body["detail"]!.GetValue<string>());
            Assert.Equal((1, 0, 0), (body["handoffs_in_progress"]!.GetValue<int>(), body["reports"]!.AsArray().Count, body["workflows"]!.AsArray().Count));
        }

        await CancelQueuedPassAsync();
        using var removed = await RemoveAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task A_hand_off_nobody_could_attribute_to_a_connection_holds_the_only_connection_of_its_kind()
    {
        var id = await AddDelunoAsync();
        await HandOffAsync("h1");
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET connection_id = NULL");

        using var refused = await RemoveAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1, (await Json(refused))["handoffs_in_progress"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_hand_off_nobody_could_attribute_does_not_hold_a_connection_when_another_of_its_kind_remains()
    {
        var id = await AddDelunoAsync();
        await AddDelunoAsync("http://192.0.2.11:5099");
        await HandOffAsync("h1");
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET connection_id = NULL");

        using var removed = await RemoveAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task A_hand_off_that_belongs_to_another_connection_does_not_hold_this_one()
    {
        var first = await AddDelunoAsync();
        var second = await AddDelunoAsync("http://192.0.2.11:5099");
        await HandOffAsync("h1");
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET connection_id = $c", ("$c", second));

        using var removed = await RemoveAsync(first);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task A_report_Weir_still_owes_the_manager_holds_the_connection_until_it_is_delivered()
    {
        var id = await AddDelunoAsync();
        await HandOffAsync("h1");
        await CancelQueuedPassAsync();
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET pending_report_json = '{}'");

        using (var refused = await RemoveAsync(id))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await Json(refused);
            Assert.Equal("connection_in_use", body["code"]!.GetValue<string>());
            Assert.Equal(
                "Weir still owes Deluno on 192.0.2.10 a report on a finished hand-off. Nothing was removed. " +
                "Before removing it, let Weir deliver the report it owes or give up on it.",
                body["detail"]!.GetValue<string>());
            Assert.Equal((0, """[{"handoff_id":"h1","name":"film.mkv"}]"""), (body["handoffs_in_progress"]!.GetValue<int>(), body["reports"]!.ToJsonString()));
        }

        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET pending_report_json = NULL");
        using var removed = await RemoveAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    private const string OwedReportJson = """{"body":{},"library_id":1,"relative_media_path":"Film/film.mkv","relative_media_paths":["Film/film.mkv"]}""";

    private async Task OweReportAsync(string handoffId)
    {
        await TestDatabase.ExecuteAsync(
            _server, "UPDATE media_manager_handoffs SET pending_report_json = $report WHERE handoff_id = $id", ("$report", OwedReportJson), ("$id", handoffId));
        await TestDatabase.ExecuteAsync(
            _server,
            "UPDATE files SET status_reason = $waiting WHERE relative_path = 'Film/film.mkv'",
            ("$waiting", "Waiting for Deluno on 192.0.2.10, which is not answering. Weir will tell it this file is ready when it answers."));
    }

    private async Task<HttpResponseMessage> GiveUpReportsAsync(long connectionId) =>
        await _client.PostAsync($"{Connections}/{connectionId}/give-up-reports", new { csrf_token = await _client.CsrfAsync() });

    [Fact]
    public async Task Giving_up_on_owed_reports_stops_owing_them_says_so_on_the_file_records_what_was_dropped_and_frees_the_connection()
    {
        var id = await AddDelunoAsync();
        await HandOffAsync("h1");
        await CancelQueuedPassAsync();
        await OweReportAsync("h1");
        using (var refused = await RemoveAsync(id))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        using var gave = await GiveUpReportsAsync(id);

        Assert.Equal(HttpStatusCode.OK, gave.StatusCode);
        Assert.Equal("""{"reports":[{"handoff_id":"h1","name":"film.mkv"}]}""", (await Json(gave)).ToJsonString());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
        Assert.Equal(
            "Weir gave up on telling Deluno on 192.0.2.10 this file is ready, as you asked.",
            await TestDatabase.ScalarStringAsync(_server, "SELECT status_reason FROM files WHERE relative_path = 'Film/film.mkv'"));
        Assert.Equal(
            "Weir gave up on a report it owed Deluno on 192.0.2.10",
            await TestDatabase.ScalarStringAsync(_server, "SELECT title FROM activity_events WHERE event_type = 'processing.handoff_reports_dropped'"));
        var detail = JsonNode.Parse((await TestDatabase.ScalarStringAsync(_server, "SELECT detail FROM activity_events WHERE event_type = 'processing.handoff_reports_dropped'"))!)!;
        Assert.Equal("""[{"handoff_id":"h1","name":"film.mkv"}]""", detail["reports"]!.ToJsonString());
        Assert.Equal("Weir will not tell Deluno on 192.0.2.10 about this finished hand-off: film.mkv.", detail["message"]!.GetValue<string>());
        using var removed = await RemoveAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task Giving_up_with_nothing_owed_changes_nothing_and_records_nothing()
    {
        var id = await AddDelunoAsync();

        using var gave = await GiveUpReportsAsync(id);

        Assert.Equal(HttpStatusCode.OK, gave.StatusCode);
        Assert.Equal("""{"reports":[]}""", (await Json(gave)).ToJsonString());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_reports_dropped'"));
    }

    [Fact]
    public async Task Giving_up_drops_only_this_connections_reports()
    {
        var first = await AddDelunoAsync();
        var second = await AddDelunoAsync("http://192.0.2.11:5099");
        await HandOffAsync("h1");
        await CancelQueuedPassAsync();
        await OweReportAsync("h1");
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET connection_id = $c", ("$c", second));

        using var gave = await GiveUpReportsAsync(first);

        Assert.Equal("""{"reports":[]}""", (await Json(gave)).ToJsonString());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
    }

    [Fact]
    public async Task Giving_up_on_several_reports_counts_them_in_the_title()
    {
        var id = await AddDelunoAsync();
        Directory.CreateDirectory(Path.Join(_watched, "Second"));
        await File.WriteAllTextAsync(Path.Join(_watched, "Second", "film.mkv"), Original);
        await HandOffAsync("h1");
        await HandOffAsync("h2", "Second");
        await TestDatabase.ExecuteAsync(_server, "UPDATE jobs SET status = 'cancelled' WHERE job_kind = 'processing.file.remux_pass.v1'");
        await TestDatabase.ExecuteAsync(_server, "UPDATE media_manager_handoffs SET state = 'cancelled', pending_report_json = $report", ("$report", OwedReportJson));

        using var gave = await GiveUpReportsAsync(id);

        Assert.Equal(2, (await Json(gave))["reports"]!.AsArray().Count);
        Assert.Equal(
            "Weir gave up on 2 reports it owed Deluno on 192.0.2.10",
            await TestDatabase.ScalarStringAsync(_server, "SELECT title FROM activity_events WHERE event_type = 'processing.handoff_reports_dropped'"));
    }

    [Fact]
    public async Task Everything_in_the_way_is_named_together()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);
        Directory.CreateDirectory(Path.Join(_watched, "Second"));
        await File.WriteAllTextAsync(Path.Join(_watched, "Second", "film.mkv"), Original);
        await HandOffAsync("h1");
        await HandOffAsync("h2", "Second");

        using var refused = await RemoveAsync(id);

        Assert.Equal(
            "Movies still comes from Deluno on 192.0.2.10. Weir is still working on 2 hand-offs from it. Nothing was removed. " +
            "Before removing it, unlink or remove that workflow and let the hand-offs finish or cancel them.",
            await Detail(refused));
    }

    [Fact]
    public async Task Unlinking_keeps_the_workflows_switched_off_and_leaves_their_folders_alone_and_the_connection_can_then_go()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);
        await LinkAsync("tv", id);
        await TestDatabase.ExecuteAsync(_server, "UPDATE libraries SET remove_original_after_success = 1");

        using var unlinked = await UnlinkWorkflowsAsync(id);

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal("""{"workflows":[{"id":1,"name":"Movies"},{"id":2,"name":"TV"}]}""", (await Json(unlinked)).ToJsonString());
        Assert.Equal((0L, 0L, 2L), (await CountAsync("SELECT count(*) FROM library_manager_links"), await CountAsync("SELECT count(*) FROM libraries WHERE enabled = 1"), await CountAsync("SELECT count(*) FROM libraries")));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM media_manager_connections"));
        AssertOriginalUntouched();

        using var removed = await RemoveAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM libraries"));
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task A_workflow_still_linked_to_another_connection_stays_linked_to_it_and_keeps_running()
    {
        var id = await AddDelunoAsync();
        var other = await AddDelunoAsync("http://192.0.2.11:5099");
        await LinkAsync("movie", id);
        await LinkAsync("movie", other);

        using var unlinked = await UnlinkWorkflowsAsync(id);

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM library_manager_links WHERE connection_id = {other}"));
        Assert.Equal("""{"workflows":[{"id":1,"name":"Movies"}]}""", (await Json(unlinked)).ToJsonString());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM libraries WHERE enabled = 0"));
    }

    [Fact]
    public async Task Unlinking_with_nothing_linked_changes_nothing()
    {
        var id = await AddDelunoAsync();

        using var unlinked = await UnlinkWorkflowsAsync(id);

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal("""{"workflows":[]}""", (await Json(unlinked)).ToJsonString());
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM libraries WHERE enabled = 1"));
    }

    [Fact]
    public async Task Unlinking_is_refused_while_a_hand_off_is_still_in_progress_because_the_original_is_not_Weirs_to_release_yet()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);
        await HandOffAsync("h1");

        using var refused = await UnlinkWorkflowsAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await Json(refused);
        Assert.Equal("handoffs_in_progress", body["code"]!.GetValue<string>());
        Assert.Equal(
            "Weir is still working on 1 hand-off from Deluno on 192.0.2.10, so its workflows stay linked. Nothing was changed. " +
            "Let the hand-off finish or cancel it first.",
            body["detail"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM library_manager_links"));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM libraries WHERE enabled = 1"));
    }

    [Fact]
    public async Task Removing_the_workflows_first_leaves_their_folders_alone_and_the_connection_can_then_go()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);
        await LinkAsync("tv", id);
        // The scans queued at start would hold the workflows: a workflow with jobs queued or running is not removed.
        await TestDatabase.ExecuteAsync(_server, "DELETE FROM jobs");
        foreach (var workflow in new[] { 1, 2 })
        {
            using var gone = await _client.SendAsync(HttpMethod.Delete, $"{Libraries}/{workflow}", new { csrf_token = await _client.CsrfAsync() });
            Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        }

        using var removed = await RemoveAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal((0L, 0L), (await CountAsync("SELECT count(*) FROM media_manager_connections"), await CountAsync("SELECT count(*) FROM library_manager_links")));
        AssertOriginalUntouched();
    }

    [Fact]
    public async Task The_database_itself_refuses_to_drop_a_connection_a_workflow_is_still_linked_to()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => TestDatabase.ExecuteAsync(_server, "DELETE FROM media_manager_connections WHERE id = $id", ("$id", id)));

        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM library_manager_links"));
    }

    [Fact]
    public async Task Removing_a_workflow_still_takes_its_links_with_it()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);

        await TestDatabase.ExecuteAsync(_server, "DELETE FROM libraries WHERE media_type = 'movie'");

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM library_manager_links"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task Both_steps_and_the_removal_need_a_signed_in_operator_and_a_valid_token()
    {
        var id = await AddDelunoAsync();
        var signedOut = new ApiTestClient(_server);

        using var noSession = await signedOut.SendAsync(HttpMethod.Delete, $"{Connections}/{id}", new { csrf_token = "x" });
        using var unlinkNoSession = await signedOut.PostAsync($"{Connections}/{id}/unlink-workflows", new { csrf_token = "x" });
        using var badToken = await _client.PostAsync($"{Connections}/{id}/unlink-workflows", new { csrf_token = "bad" });
        using var missing = await _client.PostAsync($"{Connections}/99/unlink-workflows", new { csrf_token = await _client.CsrfAsync() });
        using var giveUpNoSession = await signedOut.PostAsync($"{Connections}/{id}/give-up-reports", new { csrf_token = "x" });
        using var giveUpBadToken = await _client.PostAsync($"{Connections}/{id}/give-up-reports", new { csrf_token = "bad" });
        using var giveUpMissing = await _client.PostAsync($"{Connections}/99/give-up-reports", new { csrf_token = await _client.CsrfAsync() });

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, HttpStatusCode.NotFound],
            new[] { noSession.StatusCode, unlinkNoSession.StatusCode, badToken.StatusCode, missing.StatusCode, giveUpNoSession.StatusCode, giveUpBadToken.StatusCode, giveUpMissing.StatusCode });
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task The_refusal_is_valid_json_with_only_the_documented_members()
    {
        var id = await AddDelunoAsync();
        await LinkAsync("movie", id);

        using var refused = await RemoveAsync(id);

        Assert.Equal(
            ["code", "detail", "handoffs_in_progress", "reports", "workflows"],
            ((JsonObject)await Json(refused)).Select(member => member.Key).Order(StringComparer.Ordinal));
    }
}
