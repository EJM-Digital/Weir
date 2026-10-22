using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Api.Tests.Platform;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// The <c>connection.activity</c> frame on the Activity stream, and what puts a connection on it: a test through the API, a
/// media manager calling Weir, and the usage the connection list reports afterwards.
/// </summary>
public sealed class ConnectionActivityApiTests
{
    private const string Connections = "/api/v1/media-managers/connections";
    private static readonly Timestamp Noon = Timestamp.FromUtc(new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));

    private static ConnectionActivity Activity(ConnectionKind kind, ConnectionPhase phase, ConnectionDirection direction, long? milliseconds) =>
        new(new ConnectionRef(kind, 3), phase, direction, Noon, milliseconds);

    [Theory]
    [InlineData(ConnectionKind.MediaManager, ConnectionPhase.Asked, ConnectionDirection.Outbound, null,
        "event: connection.activity\ndata: {\"kind\":\"media_manager\",\"id\":3,\"phase\":\"asked\",\"direction\":\"outbound\",\"at\":\"2026-10-02T12:00:00Z\",\"ms\":null}\n\n")]
    [InlineData(ConnectionKind.MediaManager, ConnectionPhase.Answered, ConnectionDirection.Outbound, 84L,
        "event: connection.activity\ndata: {\"kind\":\"media_manager\",\"id\":3,\"phase\":\"answered\",\"direction\":\"outbound\",\"at\":\"2026-10-02T12:00:00Z\",\"ms\":84}\n\n")]
    [InlineData(ConnectionKind.DownloadClient, ConnectionPhase.Failed, ConnectionDirection.Outbound, 10000L,
        "event: connection.activity\ndata: {\"kind\":\"download_client\",\"id\":3,\"phase\":\"failed\",\"direction\":\"outbound\",\"at\":\"2026-10-02T12:00:00Z\",\"ms\":10000}\n\n")]
    [InlineData(ConnectionKind.MediaManager, ConnectionPhase.Answered, ConnectionDirection.Inbound, null,
        "event: connection.activity\ndata: {\"kind\":\"media_manager\",\"id\":3,\"phase\":\"answered\",\"direction\":\"inbound\",\"at\":\"2026-10-02T12:00:00Z\",\"ms\":null}\n\n")]
    public void A_frame_names_the_connection_the_phase_the_direction_and_how_long_it_took(
        ConnectionKind kind, ConnectionPhase phase, ConnectionDirection direction, long? milliseconds, string expected) =>
        Assert.Equal(expected, ConnectionActivityFrames.Frame(Activity(kind, phase, direction, milliseconds)));

    private static async Task<(WeirTestServer Server, ApiTestClient Client, ScriptedManager Manager)> StartAsync()
    {
        var manager = new ScriptedManager();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(
                provider => manager.ReportingTo(provider.GetRequiredService<ConnectionActivityHub>(), provider.GetRequiredService<TimeProvider>())));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client, manager);
    }

    private static async Task<JsonNode> CreateRadarrAsync(ApiTestClient client)
    {
        using var response = await client.PostAsync(Connections, new { csrf_token = await client.CsrfAsync(), kind = "radarr", base_url = "http://192.0.2.20:7878", api_key = "key" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    private static async Task<StreamReader> OpenStreamAsync(WeirTestServer server, ApiTestClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/activity/stream");
        request.Headers.Add("Cookie", string.Join("; ", client.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    /// <summary>
    /// The next <c>connection.activity</c> frame's data that satisfies <paramref name="wanted"/> (any frame, when omitted), repeating
    /// <paramref name="provoke"/> until one arrives so the test waits out the stream's own start. Connecting a manager also starts a
    /// background workflow sync that calls it, so a test about something else names the frame it wants.
    /// </summary>
    private static async Task<JsonNode> NextConnectionFrameAsync(StreamReader reader, Func<Task> provoke, Func<JsonNode, bool>? wanted = null)
    {
        var pending = ReadConnectionFrameAsync(reader, wanted ?? (_ => true));
        await Eventually.ThatAsync(
            async () =>
            {
                if (!pending.IsCompleted)
                {
                    await provoke();
                }

                return pending.IsCompleted;
            });
        return await pending;
    }

    private static async Task<JsonNode> ReadConnectionFrameAsync(StreamReader reader, Func<JsonNode, bool> wanted)
    {
        string? eventName = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName == "connection.activity")
            {
                var frame = JsonNode.Parse(line["data: ".Length..])!;
                if (wanted(frame))
                {
                    return frame;
                }
            }
        }

        throw new InvalidOperationException("The stream ended.");
    }

    [Fact]
    public async Task A_frame_published_in_the_server_reaches_an_open_stream()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var hub = server.Services.GetRequiredService<ConnectionActivityHub>();
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextConnectionFrameAsync(reader, () =>
        {
            hub.Publish(new ConnectionRef(ConnectionKind.DownloadClient, 5), ConnectionPhase.Answered, ConnectionDirection.Outbound, 41);
            return Task.CompletedTask;
        });

        Assert.Equal(("download_client", 5, "answered", "outbound", 41), (
            frame["kind"]!.GetValue<string>(), frame["id"]!.GetValue<int>(), frame["phase"]!.GetValue<string>(), frame["direction"]!.GetValue<string>(), frame["ms"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Testing_a_connection_lights_it_on_the_stream_and_leaves_its_answer_time_in_the_list()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        manager.Json(HttpMethod.Get, "/api/v3/system/status", "{}");
        var radarr = await CreateRadarrAsync(client);
        var id = radarr["id"]!.GetValue<int>();
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextConnectionFrameAsync(reader, async () =>
        {
            using var tested = await client.PostAsync($"{Connections}/{id}/test", new { csrf_token = await client.CsrfAsync() });
            Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        });

        Assert.Equal(("media_manager", id, "outbound"), (frame["kind"]!.GetValue<string>(), frame["id"]!.GetValue<int>(), frame["direction"]!.GetValue<string>()));
        using var listed = await client.GetAsync($"{Connections}/{id}");
        var row = await Json(listed);
        Assert.NotNull(row["last_answer_ms"]);
        Assert.NotNull(row["last_used_at"]);
        Assert.EndsWith("Z", row["last_used_at"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_calling_the_webhook_with_its_secret_lights_its_connection_as_an_inbound_answer()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var radarr = await CreateRadarrAsync(client);
        var id = radarr["id"]!.GetValue<int>();
        using var generated = await client.PostAsync($"{Connections}/{id}/webhook-secret", new { csrf_token = await client.CsrfAsync() });
        var secret = (await Json(generated))["webhook_secret"]!.GetValue<string>();
        var anonymous = new ApiTestClient(server);
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextConnectionFrameAsync(reader, async () =>
        {
            using var posted = await anonymous.PostAsync("/api/v1/intake/webhook/radarr", new { eventType = "Test" }, new Dictionary<string, string> { ["X-Webhook-Secret"] = secret });
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }, wanted: candidate => candidate["direction"]!.GetValue<string>() == "inbound");

        Assert.Equal(("media_manager", id, "answered", "inbound"), (
            frame["kind"]!.GetValue<string>(), frame["id"]!.GetValue<int>(), frame["phase"]!.GetValue<string>(), frame["direction"]!.GetValue<string>()));
        Assert.Null(frame["ms"]);
    }
}
