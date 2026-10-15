using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Removing an alert channel takes that channel and nothing else, only with the confirmation token in <c>X-CSRF-Token</c>, and a
/// channel that is gone is never posted to again: the next alert reaches only the channels that are left.
/// </summary>
public sealed class NotificationChannelRemovalApiTests
{
    private const string Channels = "/api/v1/suite/notification-channels";
    private const string Kept = "https://hooks.example.com/kept";
    private const string Removed = "https://hooks.example.com/removed";

    private static async Task<(WeirTestServer Server, ApiTestClient Client, RecordingPoster Poster)> StartAsync()
    {
        var poster = new RecordingPoster();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IExternalJsonPoster>(poster));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client, poster);
    }

    private static async Task<long> CreateChannelAsync(ApiTestClient client, string label, string url)
    {
        using var created = await client.PostAsync(
            Channels, new { csrf_token = await client.CsrfAsync(), label, provider = "webhook", url, events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created))["id"]!.GetValue<long>();
    }

    private static async Task<HttpResponseMessage> DeleteAsync(ApiTestClient client, long id, bool withToken)
    {
        var headers = withToken ? new Dictionary<string, string> { ["X-CSRF-Token"] = await client.CsrfAsync() } : null;
        return await client.SendAsync(HttpMethod.Delete, $"{Channels}/{id}", headers: headers);
    }

    private static async Task<List<string>> UrlsAfterAFailedJobAsync(WeirTestServer server, RecordingPoster poster)
    {
        await TestDatabase.ExecuteAsync(server, "INSERT INTO jobs (dedupe_key, job_kind, status) VALUES ('alert-source', 'processing.file.remux_pass.v1', 'failed')");
        var jobId = await TestDatabase.ScalarAsync(server, "SELECT id FROM jobs WHERE dedupe_key = 'alert-source'");
        var dispatcher = server.Services.GetRequiredService<NotificationDispatcher>();
        dispatcher.DispatchJobNotification(server.Services.GetRequiredService<SqliteDatabase>(), "processing", "failed", jobId, "processing.file.remux_pass.v1", (_, _) => { });
        await dispatcher.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return poster.Urls;
    }

    [Fact]
    public async Task Without_the_token_nothing_is_removed_and_with_it_only_that_channel_goes()
    {
        var (server, client, poster) = await StartAsync();
        await using var stop = server;
        var removed = await CreateChannelAsync(client, "Removed", Removed);
        var kept = await CreateChannelAsync(client, "Kept", Kept);

        using var refused = await DeleteAsync(client, removed, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels"));

        using var deleted = await DeleteAsync(client, removed, withToken: true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels WHERE id = $id", ("$id", removed)));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels WHERE id = $id AND label = 'Kept' AND enabled = 1", ("$id", kept)));
    }

    [Fact]
    public async Task A_channel_that_is_gone_is_never_posted_to_again_and_the_one_that_stays_still_is()
    {
        var (server, client, poster) = await StartAsync();
        await using var stop = server;
        var removed = await CreateChannelAsync(client, "Removed", Removed);
        await CreateChannelAsync(client, "Kept", Kept);
        using var deleted = await DeleteAsync(client, removed, withToken: true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var urls = await UrlsAfterAFailedJobAsync(server, poster);

        Assert.Equal([Kept], urls);
    }

    [Fact]
    public async Task A_channel_that_is_gone_cannot_be_tested_and_nothing_is_posted()
    {
        var (server, client, poster) = await StartAsync();
        await using var stop = server;
        var removed = await CreateChannelAsync(client, "Removed", Removed);
        using var deleted = await DeleteAsync(client, removed, withToken: true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var again = await DeleteAsync(client, removed, withToken: true);
        using var tested = await client.PostAsync($"{Channels}/{removed}/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, tested.StatusCode);
        Assert.Empty(poster.Urls);
    }

    private sealed class RecordingPoster : IExternalJsonPoster
    {
        private readonly ConcurrentQueue<string> _urls = new();

        public List<string> Urls => [.. _urls];

        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _urls.Enqueue(url);
            return Task.FromResult(204);
        }
    }
}
