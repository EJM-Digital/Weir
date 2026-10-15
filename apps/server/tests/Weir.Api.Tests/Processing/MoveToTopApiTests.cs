using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.Jobs;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Move to top" on a file waiting in the queue: its job is claimed ahead of every other waiting job. A file with nothing waiting
/// is told so, a file Weir has no record of is refused, and the request needs an operator's session.
/// </summary>
public sealed class MoveToTopApiTests
{
    private const string Kind = "processing.file.remux_pass.v1";

    private static string MovePath(long fileId) => $"/api/v1/processing/files/{fileId}/move-to-top";

    private static async Task<(WeirTestServer Server, ApiTestClient Client, long LibraryId)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server, "INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        return (server, client, libraryId);
    }

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string relativePath) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'unprocessed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath));

    /// <summary>A pass waiting in the queue for the file, the way a scan queues it.</summary>
    private static Task<long> SeedPassAsync(WeirTestServer server, long libraryId, string relativePath, string status = "pending") =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, lease_owner, lease_expires_at) " +
            "VALUES ($key, $kind, $payload, $status, CASE $status WHEN 'leased' THEN 'a-worker' END, CASE $status WHEN 'leased' THEN '2999-01-01 00:00:00' END) RETURNING id",
            ("$key", $"{Kind}:{relativePath}"),
            ("$kind", Kind),
            ("$payload", $$"""{"relative_media_path": "{{relativePath}}", "media_scope": "movie", "library_id": {{libraryId}}}"""),
            ("$status", status));

    private static async Task<HttpResponseMessage> MoveAsync(ApiTestClient client, long fileId) =>
        await client.PostAsync(MovePath(fileId), new { csrf_token = await client.CsrfAsync() });

    /// <summary>The ids of the jobs in the order the workers would take them.</summary>
    private static async Task<List<long>> ClaimOrderAsync(WeirTestServer server)
    {
        var jobs = server.Services.GetRequiredService<ProcessingJobStore>();
        var order = new List<long>();
        while (await jobs.ClaimNextAsync("claim-order-test", DateTimeOffset.UtcNow.AddMinutes(5), kinds: new ClaimableKinds([Kind], IncludeRefusedKinds: false)) is { } claimed)
        {
            order.Add(claimed.Id);
        }

        return order;
    }

    [Fact]
    public async Task A_file_moved_to_the_top_is_claimed_before_the_files_that_were_queued_ahead_of_it()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var first = await SeedPassAsync(server, libraryId, "Alien/alien.mkv");
        var second = await SeedPassAsync(server, libraryId, "Heat/heat.mkv");
        var third = await SeedPassAsync(server, libraryId, "Up/up.mkv");
        await SeedFileAsync(server, libraryId, "Alien/alien.mkv");
        await SeedFileAsync(server, libraryId, "Heat/heat.mkv");
        var upId = await SeedFileAsync(server, libraryId, "Up/up.mkv");

        using var response = await MoveAsync(client, upId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.True(body["moved"]!.GetValue<bool>());
        Assert.Equal("Moved to the front of the queue. It starts as soon as there is capacity for it.", body["detail"]!.GetValue<string>());
        Assert.Equal([third, first, second], await ClaimOrderAsync(server));
    }

    [Fact]
    public async Task The_file_moved_last_goes_ahead_of_one_moved_before_it()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var alien = await SeedPassAsync(server, libraryId, "Alien/alien.mkv");
        var heat = await SeedPassAsync(server, libraryId, "Heat/heat.mkv");
        var up = await SeedPassAsync(server, libraryId, "Up/up.mkv");
        var alienId = await SeedFileAsync(server, libraryId, "Alien/alien.mkv");
        var heatId = await SeedFileAsync(server, libraryId, "Heat/heat.mkv");
        await SeedFileAsync(server, libraryId, "Up/up.mkv");

        using (await MoveAsync(client, heatId))
        {
        }

        using (await MoveAsync(client, alienId))
        {
        }

        Assert.Equal([alien, heat, up], await ClaimOrderAsync(server));
    }

    [Fact]
    public async Task A_file_with_no_waiting_pass_is_told_there_is_nothing_to_move_and_the_queue_is_left_alone()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var waiting = await SeedPassAsync(server, libraryId, "Alien/alien.mkv");
        var running = await SeedPassAsync(server, libraryId, "Heat/heat.mkv", status: "leased");
        await SeedFileAsync(server, libraryId, "Alien/alien.mkv");
        var heatId = await SeedFileAsync(server, libraryId, "Heat/heat.mkv");
        var idleId = await SeedFileAsync(server, libraryId, "Up/up.mkv");

        foreach (var fileId in new[] { heatId, idleId })
        {
            using var response = await MoveAsync(client, fileId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await Json(response);
            Assert.False(body["moved"]!.GetValue<bool>());
            Assert.Equal(
                "There is no queued work for this file to move. It may already be running, or it may not have been picked up by a scan yet.",
                body["detail"]!.GetValue<string>());
        }

        Assert.Equal([waiting], await ClaimOrderAsync(server));
        Assert.Equal("leased", await TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", running)));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT max(priority) FROM jobs WHERE job_kind = $kind", ("$kind", Kind)));
    }

    [Fact]
    public async Task A_file_Weir_has_no_record_of_is_refused_with_not_found()
    {
        var (server, client, _) = await StartAsync();
        await using var __ = server;

        using var response = await MoveAsync(client, 99999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Weir has no record of that file.", await Detail(response));
    }

    [Fact]
    public async Task Without_an_operators_session_and_token_a_file_cannot_be_moved()
    {
        var (server, _, libraryId) = await StartAsync();
        await using var __ = server;
        var job = await SeedPassAsync(server, libraryId, "Alien/alien.mkv");
        var other = await SeedPassAsync(server, libraryId, "Heat/heat.mkv");
        var heatId = await SeedFileAsync(server, libraryId, "Heat/heat.mkv");

        await OperatorPostRefusals.AssertAsync(server, MovePath(heatId), csrf => new { csrf_token = csrf });

        Assert.Equal([job, other], await ClaimOrderAsync(server));
    }
}
