using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// #633 over real HTTP: "Files at once" goes to 10, a library can follow it, the resolution budget is a switch, and
/// <c>GET /processing/files-at-once</c> says what waiting files are waiting for.
/// </summary>
public sealed class ProcessingFilesAtOnceApiTests
{
    private const string Remux = "processing.file.remux_pass.v1";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAsync(params (string Name, string Value)[] variables)
    {
        var server = await ApiTestClient.StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task Files_at_once_saves_up_to_ten_and_refuses_eleven()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var ten = await client.PutAsync("/api/v1/processing/operator-settings", new { csrf_token = await client.CsrfAsync(), max_concurrent_files = 10 });
        Assert.Equal(HttpStatusCode.OK, ten.StatusCode);
        Assert.Equal(10, (await ApiTestClient.Json(ten))["max_concurrent_files"]!.GetValue<int>());

        using var eleven = await client.PutAsync("/api/v1/processing/operator-settings", new { csrf_token = await client.CsrfAsync(), max_concurrent_files = 11 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eleven.StatusCode);
    }

    [Fact]
    public async Task The_resolution_budget_is_off_until_it_is_switched_on()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var before = await client.GetAsync("/api/v1/processing/operator-settings");
        Assert.False((await ApiTestClient.Json(before))["runner_budget_enabled"]!.GetValue<bool>());

        using var saved = await client.PutAsync("/api/v1/processing/operator-settings", new { csrf_token = await client.CsrfAsync(), runner_budget_enabled = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await ApiTestClient.Json(saved))["runner_budget_enabled"]!.GetValue<bool>());
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT runner_budget_enabled FROM operator_settings WHERE id = 1"));
    }

    [Fact]
    public async Task A_new_library_follows_files_at_once_and_can_be_held_lower()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        await SetFilesAtOnceAsync(client, 3);

        using var created = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Anime", media_type = "movie", watched_folder = @"c:\anime-in", output_folder = @"c:\anime-out" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(0, (await ApiTestClient.Json(created))["max_concurrent_files"]!.GetValue<int>());

        using var held = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Kids", media_type = "movie", watched_folder = @"c:\kids-in", output_folder = @"c:\kids-out", max_concurrent_files = 2 });
        Assert.Equal(2, (await ApiTestClient.Json(held))["max_concurrent_files"]!.GetValue<int>());

        using var tooMany = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "More", media_type = "movie", watched_folder = @"c:\more-in", output_folder = @"c:\more-out", max_concurrent_files = 11 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
    }

    [Fact]
    public async Task A_workflow_cannot_ask_for_more_at_once_than_the_total()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        await SetFilesAtOnceAsync(client, 3);

        using var tooMany = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Kids", media_type = "movie", watched_folder = @"c:\kids-in", output_folder = @"c:\kids-out", max_concurrent_files = 4 });

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("cannot be more than the 3 Weir runs in total", await ApiTestClient.Detail(tooMany), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_above_a_lowered_total_reports_the_limit_it_gets_and_still_saves_its_other_settings()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        await SetFilesAtOnceAsync(client, 4);
        using var created = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Kids", media_type = "movie", watched_folder = @"c:\kids-in", output_folder = @"c:\kids-out", max_concurrent_files = 3 });
        var stored = await ApiTestClient.Json(created);
        await SetFilesAtOnceAsync(client, 2);

        using var listed = await client.GetAsync("/api/v1/processing/libraries");
        var reported = (await ApiTestClient.Json(listed)).AsArray().Single(library => library!["name"]!.GetValue<string>() == "Kids")!;
        Assert.Equal((3, 2), (reported["max_concurrent_files"]!.GetValue<int>(), reported["effective_max_concurrent_files"]!.GetValue<int>()));

        using var resaved = await client.PutAsync(
            $"/api/v1/processing/libraries/{stored["id"]!.GetValue<long>()}",
            new { csrf_token = await client.CsrfAsync(), name = "Kids", media_type = "movie", watched_folder = @"c:\kids-in", output_folder = @"c:\kids-out", max_concurrent_files = 3, priority = 5 });
        Assert.Equal(HttpStatusCode.OK, resaved.StatusCode);

        using var raised = await client.PutAsync(
            $"/api/v1/processing/libraries/{stored["id"]!.GetValue<long>()}",
            new { csrf_token = await client.CsrfAsync(), name = "Kids", media_type = "movie", watched_folder = @"c:\kids-in", output_folder = @"c:\kids-out", max_concurrent_files = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, raised.StatusCode);
    }

    private static async Task SetFilesAtOnceAsync(ApiTestClient client, int filesAtOnce)
    {
        using var saved = await client.PutAsync("/api/v1/processing/operator-settings", new { csrf_token = await client.CsrfAsync(), max_concurrent_files = filesAtOnce });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task The_read_out_says_nothing_is_waiting_on_a_quiet_server()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        Assert.Equal(
            ["files_at_once", "worker_slots", "effective_files_at_once", "running", "waiting", "waiting_for", "message", "slots_note"],
            body.AsObject().Select(pair => pair.Key));
        Assert.Equal((1, 0, "nothing", string.Empty), (body["files_at_once"]!.GetValue<int>(), body["waiting"]!.GetValue<int>(), body["waiting_for"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_read_out_answers_while_something_else_holds_the_write_lock()
    {
        // It read through the job queue's write transaction, so with several files being processed it queued behind
        // the workers' writes and failed with "database is locked" after the busy timeout.
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        using var writer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={TestDatabase.PathFor(server)};Pooling=False");
        writer.Open();
        using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE operator_settings SET max_concurrent_files = 4;";
            begin.ExecuteNonQuery();
        }

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The uncommitted 4 is not visible; the read saw the last committed value and did not wait for the writer. A read that did wait
        // could only end in "database is locked", because the writer holds the lock until the end of this test, so the answer above
        // is the whole claim and no clock is needed.
        Assert.Equal(1, (await ApiTestClient.Json(response))["files_at_once"]!.GetValue<int>());

        using var rollback = writer.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        rollback.ExecuteNonQuery();
    }

    [Fact]
    public async Task The_read_out_counts_what_is_running_and_what_is_due_and_says_why_it_waits()
    {
        // The API test server starts with its workers off, so the queued rows stay exactly as seeded - and that is the
        // reason the read-out gives. Which limit is named when workers are on is covered by FilesAtOnceTests.
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var library = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries ORDER BY id LIMIT 1");
        await TestDatabase.ExecuteAsync(server, $"UPDATE libraries SET max_concurrent_files = 1, name = 'Movies' WHERE id = {library}");
        await TestDatabase.ExecuteAsync(server, "UPDATE operator_settings SET max_concurrent_files = 3");
        var payload = $"{{\"library_id\": {library}, \"relative_media_path\": \"a.mkv\"}}";
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, lease_owner, lease_expires_at) VALUES ('running', @kind, @payload, 'leased', 'w', '2099-01-01 00:00:00.000000')",
            ("@kind", Remux), ("@payload", payload));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('waiting', @kind, @payload, 'pending')",
            ("@kind", Remux), ("@payload", payload));
        // Held back until later (a retry's backoff, a file waiting out its minimum age): waiting on no limit.
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, not_before) VALUES ('later', @kind, @payload, 'pending', '2099-01-01 00:00:00.000000')",
            ("@kind", Remux), ("@payload", payload));

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        var body = await ApiTestClient.Json(response);
        Assert.Equal((1, 1, "workers_off"), (body["running"]!.GetValue<int>(), body["waiting"]!.GetValue<int>(), body["waiting_for"]!.GetValue<string>()));
        Assert.Equal((3, 0, 0), (body["files_at_once"]!.GetValue<int>(), body["worker_slots"]!.GetValue<int>(), body["effective_files_at_once"]!.GetValue<int>()));
        Assert.Contains("workers are switched off", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_read_out_says_paused_when_a_due_file_is_waiting_on_the_suite_pause()
    {
        // #747: the contract suite proves pause blocks a worker's claim by watching for the absence of an
        // ffmpeg call; this pins the same fact through the read-out those tests now also assert on, with
        // real worker slots (unlike the API test server's default of none) so "paused" is not masked by
        // "workers_off".
        var (server, client) = await SignedInAsync(("WEIR_PROCESSING_WORKER_COUNT", "2"));
        await using var disposeServer = server;
        await TestDatabase.ExecuteAsync(server, "UPDATE suite_settings SET processing_paused = 1 WHERE id = 1");
        var library = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries ORDER BY id LIMIT 1");
        var payload = $"{{\"library_id\": {library}, \"relative_media_path\": \"a.mkv\"}}";
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('waiting', @kind, @payload, 'pending')",
            ("@kind", Remux), ("@payload", payload));

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        var body = await ApiTestClient.Json(response);
        Assert.Equal((1, "paused"), (body["waiting"]!.GetValue<int>(), body["waiting_for"]!.GetValue<string>()));
        Assert.Equal("1 file is waiting because processing is paused.", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_read_out_says_library_closed_when_a_due_file_waits_on_the_librarys_schedule()
    {
        // #747: the same pairing as the pause test above, for the other black-box "nothing happened" wait
        // the contract suite has (a library's schedule window).
        var (server, client) = await SignedInAsync(("WEIR_PROCESSING_WORKER_COUNT", "2"));
        await using var disposeServer = server;
        var library = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries ORDER BY id LIMIT 1");
        // A grid of every slot closed, not a start/end window: a window closes only the instant it names, and a
        // window that starts and ends at the same wall-clock minute is open during that minute too.
        var closedAllWeek = new string('0', Weir.Core.Jobs.ScheduleGrid.SlotsPerWeek);
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE libraries SET name = 'Movies', schedule_enabled = 1, schedule_grid = @grid WHERE id = " + library,
            ("@grid", closedAllWeek));
        var payload = $"{{\"library_id\": {library}, \"relative_media_path\": \"a.mkv\"}}";
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('waiting', @kind, @payload, 'pending')",
            ("@kind", Remux), ("@payload", payload));

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        var body = await ApiTestClient.Json(response);
        Assert.Equal((1, "library_closed"), (body["waiting"]!.GetValue<int>(), body["waiting_for"]!.GetValue<string>()));
        Assert.Equal("1 file is waiting for Movies's schedule to open.", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_running_library_scan_takes_no_file_slot_in_the_read_out()
    {
        // Upkeep has its own slots (#717), so a scan running beside a file that waits on its schedule is not "1 of 1 in use".
        var (server, client) = await SignedInAsync(("WEIR_PROCESSING_WORKER_COUNT", "2"));
        await using var disposeServer = server;
        var library = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries ORDER BY id LIMIT 1");
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE libraries SET name = 'Movies', schedule_enabled = 1, schedule_grid = @grid WHERE id = " + library,
            ("@grid", new string('0', Weir.Core.Jobs.ScheduleGrid.SlotsPerWeek)));
        var payload = $"{{\"library_id\": {library}, \"relative_media_path\": \"a.mkv\"}}";
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, lease_owner, lease_expires_at) VALUES ('scan', @kind, @payload, 'leased', 'w', '2099-01-01 00:00:00.000000')",
            ("@kind", Weir.Core.LibraryMode.LibraryModeJobKinds.ScanKind), ("@payload", $"{{\"library_id\": {library}}}"));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('waiting', @kind, @payload, 'pending')",
            ("@kind", Remux), ("@payload", payload));

        using var response = await client.GetAsync("/api/v1/processing/files-at-once");

        var body = await ApiTestClient.Json(response);
        Assert.Equal((0, 1, "library_closed"), (body["running"]!.GetValue<int>(), body["waiting"]!.GetValue<int>(), body["waiting_for"]!.GetValue<string>()));
    }
}
