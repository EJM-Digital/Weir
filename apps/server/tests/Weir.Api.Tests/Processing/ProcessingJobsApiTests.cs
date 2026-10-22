using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Api.Tests.MediaManagers;
using Weir.Api.Tests.Platform;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// The Jobs list and what an operator can do to one job: who may look, which jobs the list shows, cancelling a job that has not
/// started, and the "why is this file held?" answer worked out from what a manager says is in its queue.
/// </summary>
public sealed class ProcessingJobsApiTests
{
    private const string Kind = "processing.file.remux_pass.v1";
    private const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";
    private const string InspectionPath = "/api/v1/processing/jobs/inspection";
    private const string RadarrUrl = "http://192.0.2.20:7878";

    private static string CancelPath(long jobId) => $"/api/v1/processing/jobs/{jobId}/cancel-pending";

    private static string WhyHeldPath(long fileId) => $"/api/v1/processing/files/{fileId}/why-held";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static Task<long> SeedJobAsync(WeirTestServer server, string status, string key, string kind = Kind, string? payload = null) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($key, $kind, $payload, $status) RETURNING id",
            ("$key", key), ("$kind", kind), ("$payload", payload ?? "{}"), ("$status", status));

    private static async Task<JsonNode> InspectAsync(ApiTestClient client, string query = "")
    {
        using var response = await client.GetAsync(InspectionPath + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static IEnumerable<long> Ids(JsonNode listing) => listing["jobs"]!.AsArray().Select(job => job!["id"]!.GetValue<long>());

    [Fact]
    public async Task Anyone_signed_in_may_read_the_jobs_list_and_nobody_signed_out_may()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);

        using var asViewer = await viewer.GetAsync(InspectionPath);
        using var anonymous = await new ApiTestClient(server).GetAsync(InspectionPath);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (asViewer.StatusCode, anonymous.StatusCode));
    }

    [Fact]
    public async Task The_default_list_leaves_out_finished_scan_checks_and_says_so_while_a_status_filter_shows_everything_asked_for()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var finishedScan = await SeedJobAsync(server, ProcessingJobStatus.Completed, "scan-done", ScanKind);
        var failedScan = await SeedJobAsync(server, ProcessingJobStatus.Failed, "scan-failed", ScanKind);
        var pending = await SeedJobAsync(server, ProcessingJobStatus.Pending, "pass-pending");

        var recent = await InspectAsync(client);
        var completed = await InspectAsync(client, "?status=completed");

        Assert.True(recent["default_recent_slice"]!.GetValue<bool>());
        Assert.Equivalent(new[] { failedScan, pending }, Ids(recent));
        Assert.False(completed["default_recent_slice"]!.GetValue<bool>());
        Assert.Equal([finishedScan], Ids(completed));
    }

    [Fact]
    public async Task A_status_filter_can_name_several_statuses_and_a_limit_keeps_a_running_job_on_the_page()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var running = await SeedJobAsync(server, ProcessingJobStatus.Leased, "running");
        await SeedJobAsync(server, ProcessingJobStatus.Pending, "waiting-1");
        await SeedJobAsync(server, ProcessingJobStatus.Pending, "waiting-2");
        await SeedJobAsync(server, ProcessingJobStatus.Failed, "failed");
        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET updated_at = '2000-01-01 00:00:00' WHERE id = $id", ("$id", running));

        var active = await InspectAsync(client, "?status=pending&status=leased&limit=2");

        Assert.Equal(2, active["jobs"]!.AsArray().Count);
        Assert.Equal(running, Ids(active).First());
    }

    [Fact]
    public async Task The_live_alert_leaves_out_a_job_for_a_file_weir_has_forgotten_and_the_full_list_keeps_it()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var libraryId = await TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type, watched_folder) VALUES ('Films', 'movie', $w) RETURNING id", ("$w", Path.Join(server.Home, "watched")));
        await TestDatabase.ExecuteAsync(
            server, "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, 'Known/known.mkv', 'processing_failed', CURRENT_TIMESTAMP)", ("$lib", libraryId));
        string Payload(string path) => new JsonObject { ["relative_media_path"] = path, ["library_id"] = libraryId }.ToJsonString();
        var known = await SeedJobAsync(server, ProcessingJobStatus.Failed, "known", payload: Payload("Known/known.mkv"));
        var forgotten = await SeedJobAsync(server, ProcessingJobStatus.Failed, "forgotten", payload: Payload("Gone/gone.mkv"));
        var other = await SeedJobAsync(server, ProcessingJobStatus.Failed, "other-kind", ScanKind);

        var alert = await InspectAsync(client, "?status=failed&known_files_only=true");
        var recentAlert = await InspectAsync(client, "?known_files_only=true");
        var everything = await InspectAsync(client, "?status=failed&known_files_only=false");

        Assert.Equivalent(new[] { known, other }, Ids(alert));
        Assert.Equivalent(new[] { known, other }, Ids(recentAlert));
        Assert.Equivalent(new[] { known, forgotten, other }, Ids(everything));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=many")]
    [InlineData("?known_files_only=sometimes")]
    public async Task A_limit_or_flag_that_is_not_allowed_is_refused_before_anything_is_read(string query)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync(InspectionPath + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_status_filter_that_is_not_a_job_status_is_a_422_naming_the_value_and_the_ones_allowed()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync(InspectionPath + "?status=sleeping&status=done");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "Invalid status filter values: sleeping, done; allowed=cancelled, completed, failed, handler_ok_finalize_failed, leased, pending",
            await Detail(response));
    }

    [Fact]
    public async Task A_job_that_has_not_started_is_cancelled_and_the_answer_says_so()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var jobId = await SeedJobAsync(server, ProcessingJobStatus.Pending, "cancel-me");

        using var response = await client.PostAsync(CancelPath(jobId), new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal((true, jobId, ProcessingJobStatus.Cancelled), (body["ok"]!.GetValue<bool>(), body["job_id"]!.GetValue<long>(), body["status"]!.GetValue<string>()));
        Assert.Equal(ProcessingJobStatus.Cancelled, await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task Cancelling_a_job_that_does_not_exist_is_a_404_and_one_that_has_started_or_finished_is_a_409_left_as_it_was()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var leased = await SeedJobAsync(server, ProcessingJobStatus.Leased, "running");
        var completed = await SeedJobAsync(server, ProcessingJobStatus.Completed, "finished");

        using var missing = await client.PostAsync(CancelPath(leased + completed + 100), new { csrf_token = await client.CsrfAsync() });
        using var running = await client.PostAsync(CancelPath(leased), new { csrf_token = await client.CsrfAsync() });
        using var finished = await client.PostAsync(CancelPath(completed), new { csrf_token = await client.CsrfAsync() });

        Assert.Equal((HttpStatusCode.NotFound, "Job not found."), (missing.StatusCode, await Detail(missing)));
        const string noLongerWaiting = "This job is no longer waiting, so it can't be cancelled; the log shows where it is now.";
        Assert.Equal((HttpStatusCode.Conflict, noLongerWaiting), (running.StatusCode, await Detail(running)));
        Assert.Equal((HttpStatusCode.Conflict, noLongerWaiting), (finished.StatusCode, await Detail(finished)));
        Assert.Equal(ProcessingJobStatus.Leased, await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", leased)));
        Assert.Equal(ProcessingJobStatus.Completed, await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", completed)));
    }

    [Fact]
    public async Task Cancelling_a_job_is_turned_away_for_a_caller_who_may_not_and_changes_nothing()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;
        var jobId = await SeedJobAsync(server, ProcessingJobStatus.Pending, "keep-me");

        await OperatorPostRefusals.AssertAsync(server, CancelPath(jobId), token => new { csrf_token = token });

        Assert.Equal(ProcessingJobStatus.Pending, await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task Cancelling_a_job_with_any_other_field_in_the_request_is_a_422()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var jobId = await SeedJobAsync(server, ProcessingJobStatus.Pending, "keep-me-too");

        using var response = await client.PostAsync(CancelPath(jobId), new { csrf_token = await client.CsrfAsync(), force = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ProcessingJobStatus.Pending, await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task Recovering_a_job_that_does_not_exist_is_a_404()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync("/api/v1/processing/jobs/9999/recover-finalize-failed", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal((HttpStatusCode.NotFound, "Job not found."), (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task Asking_why_a_file_that_weir_has_no_record_of_is_held_is_a_404()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync(WhyHeldPath(404));

        Assert.Equal((HttpStatusCode.NotFound, "Weir has no record of that file."), (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task Asking_why_a_file_is_held_needs_a_whole_number_and_a_signed_in_caller()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var word = await client.GetAsync("/api/v1/processing/files/first/why-held");
        using var anonymous = await new ApiTestClient(server).GetAsync(WhyHeldPath(1));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.Unauthorized), (word.StatusCode, anonymous.StatusCode));
    }

    [Theory]
    [InlineData("99999999999999999999999999")]
    [InlineData("-99999999999999999999999999")]
    public async Task A_file_number_too_large_for_any_file_is_a_404_not_a_crash(string number)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync($"/api/v1/processing/files/{number}/why-held");

        Assert.Equal((HttpStatusCode.NotFound, "Weir has no record of that file."), (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task With_no_manager_to_ask_a_held_file_gets_no_verdict_and_the_answer_says_why()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedHeldFileAsync(server);

        using var response = await client.GetAsync(WhyHeldPath(fileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(("no_upstream_signal", "Films", "on_hold"), (body["verdict"]!.GetValue<string>(), body["library_name"]!.GetValue<string>(), body["recorded_status"]!.GetValue<string>()));
        Assert.Equal((0, 0), (body["managers_consulted"]!.GetValue<int>(), body["queue_row_count"]!.GetValue<int>()));
        Assert.NotEmpty(body["reasons"]!.AsArray());
    }

    [Fact]
    public async Task A_release_that_a_manager_still_has_in_its_queue_is_reported_as_waiting_on_the_manager()
    {
        var manager = new ScriptedManager();
        manager.Json(HttpMethod.Get, "/api/v3/queue", QueueReply("queue-with-download"));
        var (server, client) = await StartWithManagerAsync(manager);
        await using var _ = server;
        var fileId = await SeedHeldFileAsync(server, QueuedRelease);

        var body = await WhyHeldAsync(client, fileId);

        Assert.Equal("wait_upstream", body["verdict"]!.GetValue<string>());
        Assert.True(body["blocked_upstream"]!.GetValue<bool>());
        Assert.True(body["owned"]!.GetValue<bool>());
        Assert.Equal(1, body["queue_row_count"]!.GetValue<int>());
        Assert.Equal((1, 1), (body["managers_consulted"]!.GetValue<int>(), body["managers_reporting"]!.GetValue<int>()));
        Assert.Empty(body["managers_without_queue_signal"]!.AsArray());
    }

    [Fact]
    public async Task A_manager_with_an_empty_queue_holds_nothing_back()
    {
        var manager = new ScriptedManager();
        manager.Json(HttpMethod.Get, "/api/v3/queue", QueueReply("queue-empty"));
        var (server, client) = await StartWithManagerAsync(manager);
        await using var _ = server;
        var fileId = await SeedHeldFileAsync(server, QueuedRelease);

        var body = await WhyHeldAsync(client, fileId);

        Assert.Equal("not_held", body["verdict"]!.GetValue<string>());
        Assert.False(body["blocked_upstream"]!.GetValue<bool>());
        Assert.Equal(0, body["queue_row_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_file_that_is_not_the_release_in_the_managers_queue_is_not_held()
    {
        var manager = new ScriptedManager();
        manager.Json(HttpMethod.Get, "/api/v3/queue", QueueReply("queue-with-download"));
        var (server, client) = await StartWithManagerAsync(manager);
        await using var _ = server;
        var fileId = await SeedHeldFileAsync(server, "Some.Other.Film.1999.1080p.BluRay.x264-GROUP/other.mkv");

        var body = await WhyHeldAsync(client, fileId);

        Assert.Equal(("not_held", false, false, 1), (body["verdict"]!.GetValue<string>(), body["owned"]!.GetValue<bool>(), body["blocked_upstream"]!.GetValue<bool>(), body["queue_row_count"]!.GetValue<int>()));
    }

    [Theory]
    [InlineData(CandidateGateVerdict.Proceed, "proceed")]
    [InlineData(CandidateGateVerdict.WaitUpstream, "wait_upstream")]
    [InlineData(CandidateGateVerdict.NotHeld, "not_held")]
    [InlineData(CandidateGateVerdict.NoUpstreamSignal, "no_upstream_signal")]
    public void Each_verdict_has_a_fixed_name_for_clients(CandidateGateVerdict verdict, string name)
    {
        Assert.Equal(name, ProcessingJobsEndpointHandlers.VerdictName(verdict));
    }

    [Fact]
    public void A_verdict_that_does_not_exist_has_no_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingJobsEndpointHandlers.VerdictName((CandidateGateVerdict)99));
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_is_named_as_one_without_a_queue_signal()
    {
        var manager = new ScriptedManager();
        var (server, client) = await StartWithManagerAsync(manager);
        await using var _ = server;
        var fileId = await SeedHeldFileAsync(server, QueuedRelease);

        var body = await WhyHeldAsync(client, fileId);

        Assert.Equal("no_upstream_signal", body["verdict"]!.GetValue<string>());
        Assert.Equal((1, 0), (body["managers_consulted"]!.GetValue<int>(), body["managers_reporting"]!.GetValue<int>()));
        Assert.Single(body["managers_without_queue_signal"]!.AsArray());
    }

    private const string QueuedRelease = "Big.Buck.Bunny.2008.1080p.BluRay.x264-RECORDER/Big.Buck.Bunny.mkv";

    /// <summary>What a real Radarr answered when asked for its queue (<c>fixtures/recorded/radarr</c>).</summary>
    private static string QueueReply(string state) => ArrRecording.Load("radarr", state).Exchanges.Single(exchange => exchange.Request.Path == "/api/v3/queue").Reply.Text;

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartWithManagerAsync(ScriptedManager manager)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        using var connected = await client.PostAsync(
            "/api/v1/media-managers/connections",
            new { csrf_token = await client.CsrfAsync(), kind = "radarr", base_url = RadarrUrl, api_key = "the-managers-api-key" });
        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        return (server, client);
    }

    private static async Task<long> SeedHeldFileAsync(WeirTestServer server, string relativePath = "Show/episode.mkv")
    {
        var libraryId = await TestDatabase.ScalarAsync(
            server, "INSERT INTO libraries (name, media_type, watched_folder) VALUES ('Films', 'movie', $w) RETURNING id", ("$w", Path.Join(server.Home, "watched")));
        return await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'on_hold', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath));
    }

    private static async Task<JsonNode> WhyHeldAsync(ApiTestClient client, long fileId)
    {
        using var response = await client.GetAsync(WhyHeldPath(fileId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }
}
