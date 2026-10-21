using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>The media manager intake routes at their edges: a webhook with no body, and a library whose manager is no longer in the database.</summary>
public sealed class MediaManagerIntakeEdgesApiTests
{
    private const string WebhookSecret = "s3cret";

    private static readonly Dictionary<string, string> SecretHeader = new() { ["X-Webhook-Secret"] = WebhookSecret };

    private static Task<WeirTestServer> StartAsync() =>
        WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", WebhookSecret)]);

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_webhook_with_no_body_is_refused_as_missing_and_queues_nothing(string text)
    {
        await using var server = await StartAsync();

        using var response = await new ApiTestClient(server).SendAsync(
            HttpMethod.Post, "/api/v1/intake/webhook/radarr", headers: SecretHeader, content: TestDatabase.RawJson(text));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var issue = (await Json(response))["detail"]![0]!;
        Assert.Equal("missing", issue["type"]!.GetValue<string>());
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM media_manager_handoffs"));
    }

    [Fact]
    public async Task A_library_still_naming_a_manager_that_is_gone_is_published_as_set_up_by_hand()
    {
        await using var server = await StartAsync();
        await TestDatabase.ExecuteAsync(
            server,
            "PRAGMA foreign_keys = OFF; UPDATE libraries SET discovered_from_connection_id = 9999, discovered_library_key = 'lib-movies' WHERE media_type = 'movie'");

        using var response = await new ApiTestClient(server).GetAsync("/api/v1/intake/library-folders", SecretHeader);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movie = (await Json(response))["libraries"]!.AsArray().Single(library => library!["media_type"]!.GetValue<string>() == "movie")!;
        Assert.Equal(("lib-movies", false), (movie["manager_library_key"]?.GetValue<string>(), movie["folders_from_manager"]!.GetValue<bool>()));
    }
}
