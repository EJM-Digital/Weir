using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Infrastructure.Activity;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The Activity stream's <c>server.hello</c> frame and the <c>data.changed</c> frames any component can send through it.</summary>
public sealed class LiveStreamApiTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    internal static async Task<(WeirTestServer Server, ApiTestClient Client)> StartSignedInAsync(params (string Name, string Value)[] variables)
    {
        var server = await StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    internal static async Task<StreamReader> OpenStreamAsync(WeirTestServer server, ApiTestClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/activity/stream");
        request.Headers.Add("Cookie", string.Join("; ", client.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    /// <summary>The data of the next frame named <paramref name="wanted"/>, skipping every other frame the stream sends.</summary>
    internal static async Task<JsonNode> NextFrameAsync(StreamReader reader, string wanted)
    {
        string? eventName = null;
        using var timeout = new CancellationTokenSource(Patience);
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName == wanted)
            {
                return JsonNode.Parse(line["data: ".Length..])!;
            }
        }

        throw new InvalidOperationException("The stream ended.");
    }

    internal static async Task<string> NextTopicAsync(StreamReader reader) =>
        (await NextFrameAsync(reader, "data.changed"))["topic"]!.GetValue<string>();

    /// <summary>Reads topics until <paramref name="wanted"/> arrives, past the topics a request also moves, such as the queue its job joined.</summary>
    /// <summary>The next topic among <paramref name="wanted"/>, past the ones Weir's own background work publishes meanwhile.</summary>
    private static async Task<string> NextOfAsync(StreamReader reader, params string[] wanted)
    {
        string topic;
        do
        {
            topic = await NextTopicAsync(reader);
        }
        while (!wanted.Contains(topic));
        return topic;
    }

    private static async Task AssertTopicArrivesAsync(StreamReader reader, string wanted)
    {
        string topic;
        do
        {
            topic = await NextTopicAsync(reader);
        }
        while (topic != wanted);
    }

    [Fact]
    public void The_frames_are_the_documented_server_sent_events()
    {
        Assert.Equal("event: server.hello\ndata: {\"boot_id\":\"abc\"}\n\n", LiveStreamFrames.HelloFrame("abc"));
        Assert.Equal("event: data.changed\ndata: {\"topic\":\"files_at_once\"}\n\n", LiveStreamFrames.ChangedFrame(DataTopics.FilesAtOnce));
    }

    [Fact]
    public void The_checks_frame_names_when_the_server_last_looked_and_null_for_a_look_it_has_not_made()
    {
        var at = new DateTimeOffset(2026, 10, 8, 3, 4, 5, TimeSpan.Zero);

        Assert.Equal(
            "event: system.checks\ndata: {\"folders_checked_at\":null,\"readiness_checked_at\":\"2026-10-08T03:04:05Z\"}\n\n",
            SystemChecksFrames.Frame(new ServerLookTimes(null, at)));
    }

    [Fact]
    public async Task A_look_the_server_makes_reaches_an_open_stream_as_a_checks_frame()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        server.Services.GetRequiredService<ServerLooks>().ReadinessLooked();

        var frame = await NextFrameAsync(reader, "system.checks");
        Assert.NotNull(frame["readiness_checked_at"]);
    }

    [Fact]
    public async Task A_stream_that_opens_says_which_run_of_the_server_it_is_talking_to()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        using var reader = await OpenStreamAsync(server, client);

        var hello = await NextFrameAsync(reader, "server.hello");

        var bootId = hello["boot_id"]!.GetValue<string>();
        Assert.Equal(server.Services.GetRequiredService<ServerBoot>().Id, bootId);
        Assert.True(Guid.TryParse(bootId, out _));
    }

    [Fact]
    public async Task A_published_topic_reaches_every_open_stream_after_its_hello()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        using var first = await OpenStreamAsync(server, client);
        using var second = await OpenStreamAsync(server, client);
        await NextFrameAsync(first, "server.hello");
        await NextFrameAsync(second, "server.hello");

        var publisher = server.Services.GetRequiredService<DataChangePublisher>();
        publisher.Publish(DataTopics.Connections);
        publisher.Publish(DataTopics.Backups);

        string[] published = [DataTopics.Connections, DataTopics.Backups];
        Assert.Equal<IEnumerable<string>>(published, [await NextOfAsync(first, published), await NextOfAsync(first, published)]);
        Assert.Equal<IEnumerable<string>>(published, [await NextOfAsync(second, published), await NextOfAsync(second, published)]);
    }

    private static async Task<string> NewLibraryWithFoldersAsync(WeirTestServer server, ApiTestClient client)
    {
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Scanned', 'movie', '/in', '/out', '/work', 9) RETURNING id");
        using var folders = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-settings",
            new { library_folders = new[] { "/library/films" }, csrf_token = await client.CsrfAsync() });
        Assert.Equal(System.Net.HttpStatusCode.OK, folders.StatusCode);
        return $"/api/v1/processing/libraries/{libraryId}";
    }

    [Fact]
    public async Task Asking_for_a_library_scan_reaches_an_open_stream_as_a_library_scan_change()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        var library = await NewLibraryWithFoldersAsync(server, client);
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        using var scan = await client.PostAsync($"{library}/library-scan", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(System.Net.HttpStatusCode.OK, scan.StatusCode);
        await AssertTopicArrivesAsync(reader, DataTopics.LibraryScan);
    }

    [Fact]
    public async Task Turning_the_scheduled_scan_on_reaches_an_open_stream_as_a_library_scan_change()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        var library = await NewLibraryWithFoldersAsync(server, client);
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        using var scheduled = await client.PostAsync($"{library}/library-schedule", new { enabled = true, csrf_token = await client.CsrfAsync() });

        Assert.Equal(System.Net.HttpStatusCode.OK, scheduled.StatusCode);
        await AssertTopicArrivesAsync(reader, DataTopics.LibraryScan);
    }

    [Fact]
    public async Task Pausing_and_resuming_through_the_api_reach_an_open_stream_as_pause_changes()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        using var paused = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true });
        await AssertTopicArrivesAsync(reader, DataTopics.Pause);

        using var resumed = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = false });
        await AssertTopicArrivesAsync(reader, DataTopics.Pause);
    }

    [Fact]
    public async Task Removing_a_failed_file_from_the_list_reaches_an_open_stream_as_a_library_scan_change()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        var libraryId = await TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, status_reason, last_seen_at) " +
            $"VALUES ({libraryId}, 'Film/film.mkv', 'processing_failed', 'Weir could not read the audio track.', CURRENT_TIMESTAMP) RETURNING id");
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        using var removed = await client.SendAsync(HttpMethod.Delete, $"/api/v1/processing/files/{fileId}", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(System.Net.HttpStatusCode.NoContent, removed.StatusCode);
        await AssertTopicArrivesAsync(reader, DataTopics.LibraryScan);
    }

    [Fact]
    public async Task Choosing_playback_devices_reaches_an_open_stream_as_a_direct_play_devices_change()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        using var reader = await OpenStreamAsync(server, client);
        await NextFrameAsync(reader, "server.hello");

        using var saved = await client.PutAsync("/api/v1/processing/direct-play/devices", new { selected = new[] { "apple_tv_4k" }, csrf_token = await client.CsrfAsync() });

        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);
        await AssertTopicArrivesAsync(reader, DataTopics.DirectPlayDevices);
    }
}
