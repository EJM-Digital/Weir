using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Http;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Alert channels at their edges: who may send a test alert and read the metrics, what a test says when the destination takes the
/// message, refuses it or cannot be reached, and a new channel with an event Weir does not send.
/// </summary>
public sealed class NotificationChannelEdgesApiTests
{
    private const string Channels = "/api/v1/suite/notification-channels";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IExternalJsonPoster>(new AnsweringPoster()));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<long> CreateAsync(ApiTestClient client, string address)
    {
        using var created = await client.PostAsync(
            Channels, new { csrf_token = await client.CsrfAsync(), label = address, provider = "webhook", url = address, events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created))["id"]!.GetValue<long>();
    }

    [Fact]
    public async Task Who_may_send_a_test_alert_and_who_may_read_the_metrics()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var id = await CreateAsync(client, "https://hooks.example.com/taken");

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, $"{Channels}/{id}/test", Callers.Operator);
        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, "/metrics", Callers.Operator);
    }

    [Theory]
    [InlineData("https://hooks.example.com/taken", true, null)]
    [InlineData("https://hooks.example.com/refused", false, "The notification destination answered with an error (HTTP 404), so it did not take the message. Check the address.")]
    [InlineData("https://hooks.example.com/unreachable", false, "Weir could not deliver the notification. Check the destination and server logs.")]
    public async Task A_test_alert_says_whether_the_destination_took_it_and_if_not_why(string address, bool ok, string? error)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var id = await CreateAsync(client, address);

        using var response = await client.PostAsync($"{Channels}/{id}/test", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(ok, body["ok"]!.GetValue<bool>());
        Assert.Equal(error, body["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_new_channel_for_an_event_weir_does_not_send_is_refused_and_saves_nothing()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(
            Channels,
            new { csrf_token = await client.CsrfAsync(), label = "Odd", provider = "webhook", url = "https://hooks.example.com/odd", events = new[] { "no_such_event" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Unknown events: no_such_event.", await Detail(response), StringComparison.Ordinal);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels"));
    }

    /// <summary>Takes any address, refuses one ending "refused" with a 404 and fails to reach one ending "unreachable".</summary>
    private sealed class AnsweringPoster : IExternalJsonPoster
    {
        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken) =>
            url.EndsWith("unreachable", StringComparison.Ordinal)
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(url.EndsWith("refused", StringComparison.Ordinal) ? 404 : 204);
    }
}
