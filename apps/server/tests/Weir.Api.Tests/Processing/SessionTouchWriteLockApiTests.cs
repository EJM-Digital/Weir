using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// The cross-connection deadlock between a request's <see cref="UnitOfWork"/> and
/// <see cref="Weir.Infrastructure.Jobs.ProcessingJobStore.InTransactionAsync"/>.
///
/// <para>
/// <c>RequireUserAsync</c> refreshes <c>user_sessions.last_seen_at</c> once every
/// <c>SessionRules.LastSeenTouchGap</c>. That <c>UPDATE</c> opens the request's transaction and takes SQLite's
/// single write lock, which is only released by the commit in <c>ApiRoutes.RunAsync</c> — after the handler
/// returns. A handler that then calls <c>ProcessingJobStore</c> opens a second connection and issues
/// <c>BEGIN IMMEDIATE</c>, which would wait on that lock and throw once its busy timeout expired, because the
/// first connection cannot commit until the handler returns and the handler cannot return until the second
/// connection does.
/// </para>
///
/// <para>
/// Each test ages <c>last_seen_at</c> past the touch gap so the next request is guaranteed to write the
/// session, then asserts the endpoint still <em>succeeds</em>. The server under test runs with
/// <see cref="DeadlockGuardMilliseconds"/> instead of the production 30 s, so a regression to the deadlock
/// fails the request (and the test) at that guard instead of needing a wall-clock stopwatch to distinguish
/// "slow" from "hung".
/// </para>
/// </summary>
public sealed class SessionTouchWriteLockApiTests
{
    /// <summary>
    /// A hang guard, not a measured wait: far longer than any write that is merely slow on a busy machine (the sign-in and the seeding
    /// are writes too), and shorter than production's 30 s, so a regression to the cross-connection deadlock this class guards fails
    /// the request at the guard instead of costing the suite 30 real seconds per test.
    /// </summary>
    private const int DeadlockGuardMilliseconds = 10_000;

    /// <summary>
    /// Longer than the 60 s touch gap and far inside the 14-day idle window, so the next request is certain to
    /// touch the session and equally certain not to be rejected as idle-expired.
    /// </summary>
    private static readonly TimeSpan AgeBy = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task Cancelling_a_pending_job_on_the_request_that_touches_the_session_succeeds()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var jobId = await SeedJobAsync(server, "processing.file.remux_pass.v1:cancel-me", ProcessingJobStatus.Pending);

        var response = await PostAfterAgeingSessionAsync(server, client, $"/api/v1/processing/jobs/{jobId}/cancel-pending");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiTestClient.Json(response);
            Assert.Equal(ProcessingJobStatus.Cancelled, body!["status"]!.GetValue<string>());
        }

        await AssertSessionWasTouchedAsync(server);
    }

    [Fact]
    public async Task Recovering_a_finalize_failed_job_on_the_request_that_touches_the_session_succeeds()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var jobId = await SeedJobAsync(server, "processing.file.remux_pass.v1:recover-me", ProcessingJobStatus.HandlerOkFinalizeFailed);

        var response = await PostAfterAgeingSessionAsync(server, client, $"/api/v1/processing/jobs/{jobId}/recover-finalize-failed");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiTestClient.Json(response);
            Assert.Equal(ProcessingJobStatus.Completed, body!["status"]!.GetValue<string>());
        }

        await AssertSessionWasTouchedAsync(server);
    }

    [Fact]
    public async Task Moving_a_file_to_the_top_on_the_request_that_touches_the_session_succeeds()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        const string relativePath = "Movie (2020)/movie.mkv";
        var libraryId = await SeedLibraryAsync(server);
        var fileId = await SeedFileAsync(server, libraryId, relativePath);
        await SeedJobAsync(
            server,
            "processing.file.remux_pass.v1:move-me",
            ProcessingJobStatus.Pending,
            $$"""{"relative_media_path": "{{relativePath}}", "media_scope": "movie", "library_id": {{libraryId}}}""");

        var response = await PostAfterAgeingSessionAsync(server, client, $"/api/v1/processing/files/{fileId}/move-to-top");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiTestClient.Json(response);
            Assert.True(body!["moved"]!.GetValue<bool>());
        }

        await AssertSessionWasTouchedAsync(server);
    }

    [Fact]
    public async Task Requeueing_one_file_on_the_request_that_touches_the_session_succeeds()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await SeedLibraryAsync(server);
        var fileId = await SeedFileAsync(server, libraryId, "Movie (2021)/requeue-me.mkv");

        var response = await PostAfterAgeingSessionAsync(server, client, $"/api/v1/processing/files/{fileId}/requeue");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiTestClient.Json(response);
            Assert.Equal(1, body!["requeued"]!.GetValue<int>());
        }

        await AssertSessionWasTouchedAsync(server);
    }

    /// <summary>
    /// The bulk path reaches the same <c>RequeueStore</c> call. Its deliberate per-file commit already covers
    /// every iteration but the first, which is the one the session touch collides with.
    /// </summary>
    [Fact]
    public async Task Requeueing_many_files_on_the_request_that_touches_the_session_succeeds()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await SeedLibraryAsync(server);
        await SeedFileAsync(server, libraryId, "Movie (2022)/one.mkv");
        await SeedFileAsync(server, libraryId, "Movie (2022)/two.mkv");

        var response = await PostAfterAgeingSessionAsync(server, client, "/api/v1/processing/files/requeue");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiTestClient.Json(response);
            Assert.Equal(2, body!["requeued"]!.GetValue<int>());
        }

        await AssertSessionWasTouchedAsync(server);
    }

    /// <summary>
    /// A server whose <see cref="SqliteDatabase"/> uses <see cref="DeadlockGuardMilliseconds"/> instead of
    /// production's 30 s, so the deadlock this class guards against fails the request at the guard if it regresses.
    /// </summary>
    private static Task<WeirTestServer> StartServerAsync() =>
        WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton(sp =>
                new SqliteDatabase(sp.GetRequiredService<WeirOptions>().DbPath, busyTimeoutMilliseconds: DeadlockGuardMilliseconds)));

    /// <summary>
    /// Fetch the CSRF token first (that request would otherwise spend the touch), age <c>last_seen_at</c> past
    /// the gap, then send the POST that is now guaranteed to write the session before its handler runs.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAfterAgeingSessionAsync(WeirTestServer server, ApiTestClient client, string path)
    {
        var csrf = await client.CsrfAsync();
        await AgeSessionAsync(server);
        return await client.PostAsync(path, new { csrf_token = csrf });
    }

    private static Task AgeSessionAsync(WeirTestServer server) =>
        TestDatabase.ExecuteAsync(
            server,
            "UPDATE user_sessions SET last_seen_at = $seen",
            ("$seen", Timestamp.FromUtc(DateTime.UtcNow - AgeBy).ToSqlite()));

    /// <summary>
    /// Guards the guard: if the touch ever stopped happening, every test above would pass without exercising
    /// the deadlock at all.
    /// </summary>
    private static async Task AssertSessionWasTouchedAsync(WeirTestServer server)
    {
        var lastSeen = await TestDatabase.ScalarStringAsync(server, "SELECT last_seen_at FROM user_sessions");
        Assert.NotNull(lastSeen);
        Assert.True(
            Timestamp.TryFromIsoFormat(lastSeen, out var parsed),
            $"last_seen_at is not a timestamp this build can read: '{lastSeen}'.");
        Assert.True(
            DateTime.UtcNow - parsed.AsUtc < AgeBy,
            "The request did not refresh the session, so it never took the write lock and these tests prove nothing.");
    }

    private static Task<long> SeedLibraryAsync(WeirTestServer server) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type) VALUES ($name, 'movie') RETURNING id",
            ("$name", "Movies write-lock"));

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string relativePath) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) " +
            "VALUES ($lib, $path, 'processing_failed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath));

    private static Task<long> SeedJobAsync(WeirTestServer server, string dedupeKey, string status, string? payloadJson = null) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) " +
            "VALUES ($dedupe, 'processing.file.remux_pass.v1', $payload, $status) RETURNING id",
            ("$dedupe", dedupeKey), ("$payload", (object?)payloadJson ?? DBNull.Value), ("$status", status));
}
