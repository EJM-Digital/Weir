using System.Net;
using Weir.Api.Endpoints;
using Weir.Infrastructure.Activity;
using static Weir.Api.Tests.Platform.ApiTestClient;
using static Weir.Api.Tests.Platform.LiveStreamApiTests;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// What System › Security's list of signed-in browsers needs from the stream: a <c>data.changed</c> frame when a browser signs in,
/// signs out, or is signed out, so the list on another browser reads itself again. The frame names the topic and carries nothing else.
/// </summary>
public sealed class LiveSessionsApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Watcher, StreamReader Stream)> StartListeningAsync()
    {
        var (server, watcher) = await StartSignedInAsync();
        var stream = await OpenStreamAsync(server, watcher);
        await NextFrameAsync(stream, "server.hello");
        return (server, watcher, stream);
    }

    /// <summary>Reads topics until <paramref name="wanted"/> comes by, skipping the others a busy server also sends.</summary>
    private static async Task NextTopicNamedAsync(StreamReader stream, string wanted)
    {
        while (await NextTopicAsync(stream) != wanted)
        {
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ConfirmationAsync(ApiTestClient client) =>
        new Dictionary<string, string> { ["X-CSRF-Token"] = await client.CsrfAsync() };

    [Fact]
    public void The_frame_names_the_topic_and_carries_nothing_else()
    {
        Assert.Equal("event: data.changed\ndata: {\"topic\":\"sessions\"}\n\n", LiveStreamFrames.ChangedFrame(DataTopics.Sessions));
    }

    [Fact]
    public async Task Another_browser_signing_in_reaches_an_open_stream_as_a_sessions_change()
    {
        var (server, _, stream) = await StartListeningAsync();
        await using var _server = server;
        using var _stream = stream;

        await new ApiTestClient(server).SignInAsync();

        await NextTopicNamedAsync(stream, DataTopics.Sessions);
    }

    [Fact]
    public async Task Another_browser_signing_out_reaches_an_open_stream_as_a_sessions_change()
    {
        var (server, watcher) = await StartSignedInAsync();
        await using var _server = server;
        var other = new ApiTestClient(server);
        await other.SignInAsync();
        using var stream = await OpenStreamAsync(server, watcher);
        await NextFrameAsync(stream, "server.hello");

        using var loggedOut = await other.PostAsync("/api/v1/auth/logout", headers: await ConfirmationAsync(other));

        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        await NextTopicNamedAsync(stream, DataTopics.Sessions);
    }

    [Fact]
    public async Task Signing_one_browser_out_reaches_an_open_stream_as_a_sessions_change()
    {
        var (server, watcher) = await StartSignedInAsync();
        await using var _server = server;
        await new ApiTestClient(server).SignInAsync();
        using var listed = await watcher.GetAsync("/api/v1/auth/sessions");
        var otherId = (await Json(listed))["items"]!.AsArray().Single(item => !item!["current"]!.GetValue<bool>())!["session_id"]!.GetValue<string>();
        using var stream = await OpenStreamAsync(server, watcher);
        await NextFrameAsync(stream, "server.hello");

        using var revoked = await watcher.PostAsync($"/api/v1/auth/sessions/{otherId}/revoke", headers: await ConfirmationAsync(watcher));

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        await NextTopicNamedAsync(stream, DataTopics.Sessions);
    }

    [Fact]
    public async Task Signing_out_the_other_browsers_reaches_an_open_stream_as_a_sessions_change()
    {
        var (server, watcher) = await StartSignedInAsync();
        await using var _server = server;
        await new ApiTestClient(server).SignInAsync();
        using var stream = await OpenStreamAsync(server, watcher);
        await NextFrameAsync(stream, "server.hello");

        using var revoked = await watcher.PostAsync("/api/v1/auth/sessions/revoke-others", headers: await ConfirmationAsync(watcher));

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        await NextTopicNamedAsync(stream, DataTopics.Sessions);
    }

    [Fact]
    public async Task Changing_the_password_reaches_an_open_stream_as_a_sessions_change()
    {
        var (server, watcher, stream) = await StartListeningAsync();
        await using var _server = server;
        using var _stream = stream;

        using var changed = await watcher.PostAsync(
            "/api/v1/auth/change-password",
            new { csrf_token = await watcher.CsrfAsync(), current_password = AdminPassword, new_password = "another-strong-password-9" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await NextTopicNamedAsync(stream, DataTopics.Sessions);
    }
}
