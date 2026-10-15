using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.Jobs;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>"Move to top" on a file whose path another workflow holds too: only that workflow's waiting pass moves.</summary>
public sealed class MoveToTopWorkflowApiTests
{
    private const string Kind = "processing.file.remux_pass.v1";
    private const string Path = "Heat (1995)/heat.mkv";

    private static Task<long> SeedLibraryAsync(WeirTestServer server, string name) =>
        TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type) VALUES ($name, 'movie') RETURNING id", ("$name", name));

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'unprocessed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", Path));

    private static Task<long> SeedPassAsync(WeirTestServer server, long libraryId, string key) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($key, $kind, $payload, 'pending') RETURNING id",
            ("$key", $"{Kind}:{key}"),
            ("$kind", Kind),
            ("$payload", $$"""{"relative_media_path": "{{Path}}", "media_scope": "movie", "library_id": {{libraryId}}}"""));

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
    public async Task Moving_the_file_of_one_workflow_moves_only_that_workflows_pass()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var films = await SeedLibraryAsync(server, "Films");
        var kids = await SeedLibraryAsync(server, "Kids");
        await SeedFileAsync(server, films);
        var kidsFile = await SeedFileAsync(server, kids);
        var filmsPass = await SeedPassAsync(server, films, "films");
        var kidsPass = await SeedPassAsync(server, kids, "kids");

        using var response = await client.PostAsync($"/api/v1/processing/files/{kidsFile}/move-to-top", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(response))["moved"]!.GetValue<bool>());
        Assert.Equal([kidsPass, filmsPass], await ClaimOrderAsync(server));
    }

    [Fact]
    public async Task A_file_whose_workflow_has_nothing_waiting_is_not_moved_by_another_workflows_pass()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var films = await SeedLibraryAsync(server, "Films");
        var kids = await SeedLibraryAsync(server, "Kids");
        var filmsFile = await SeedFileAsync(server, films);
        await SeedFileAsync(server, kids);
        var kidsPass = await SeedPassAsync(server, kids, "kids");

        using var response = await client.PostAsync($"/api/v1/processing/files/{filmsFile}/move-to-top", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await Json(response))["moved"]!.GetValue<bool>());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT priority FROM jobs WHERE id = $id", ("$id", kidsPass)));
    }
}
