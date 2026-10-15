using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Jobs;

namespace Weir.Contract.Tests.Activity;

/// <summary>The activity freshness stream (server-sent events), and how recording an event treats retention.</summary>
[ContractArea("activity")]
public sealed class ActivityStreamTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Stream = $"{WeirClient.Api}/activity/stream";
    private const string Recent = $"{WeirClient.Api}/activity/recent";
    private const string LatestEvent = "activity.latest";

    private WeirServer Server => fixture.Server;

    [Fact]
    public async Task Activity_stream_requires_authentication()
    {
        using var client = Server.CreateClient();

        using var stream = await client.OpenStreamAsync(Stream);

        Assert.Equal(HttpStatusCode.Unauthorized, stream.Status);
    }

    [Fact]
    public async Task Activity_stream_authenticated_emits_latest_format()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var before = await LatestActivityIdAsync(admin);

        using var stream = await admin.OpenStreamAsync(Stream);

        Assert.Equal(HttpStatusCode.OK, stream.Status);
        Assert.StartsWith("text/event-stream", stream.Header("Content-Type"));
        Assert.Contains("no-store", stream.Header("Cache-Control"));
        Assert.Equal("no", stream.Header("X-Accel-Buffering"));

        Assert.Equal(["retry: 5000"], await stream.NextBlockAsync());
        // The system.stats frame is sent at once too, so the frame is looked for by name.
        var data = (await stream.NextEventNamedAsync(LatestEvent)).AsObject();
        Assert.Equal(new HashSet<string> { "latest_event_id", "activity_revision" }, data.Select(field => field.Key).ToHashSet());
        // Work Weir queued earlier can record an Activity event at any moment, so the frame carries an id from between the two reads.
        var after = await LatestActivityIdAsync(admin);
        Assert.InRange((long)data["latest_event_id"]!, before, after);
        Assert.True(data["activity_revision"]!.AsValue().TryGetValue<long>(out _));
    }

    [Fact]
    public async Task Activity_stream_emits_a_newer_id_and_revision_after_a_new_event()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var stream = await admin.OpenStreamAsync(Stream);
        var first = (await stream.NextEventNamedAsync(LatestEvent)).AsObject();

        // Signing in again records an Activity event while the stream is open. The stream holds no database
        // session, so the write is not blocked by it.
        using var other = Server.CreateClient();
        await other.LoginAsync();
        var latest = await LatestActivityIdAsync(admin);
        Assert.True(latest > (long)first["latest_event_id"]!);

        var data = (await stream.NextEventNamedAsync(LatestEvent)).AsObject();
        while ((long)data["latest_event_id"]! < latest)
        {
            data = (await stream.NextEventNamedAsync(LatestEvent)).AsObject();
        }

        Assert.Equal(latest, (long)data["latest_event_id"]!);
        Assert.True((long)data["activity_revision"]! > (long)first["activity_revision"]!);
    }

    [Fact]
    public async Task Activity_stream_says_hello_with_the_id_of_this_run_of_the_server()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var first = await admin.OpenStreamAsync(Stream);
        using var second = await admin.OpenStreamAsync(Stream);

        var hello = (await first.NextEventNamedAsync("server.hello")).AsObject();
        var again = (await second.NextEventNamedAsync("server.hello")).AsObject();

        Assert.Equal(["boot_id"], hello.Select(field => field.Key));
        Assert.True(Guid.TryParse((string)hello["boot_id"]!, out _));
        Assert.Equal((string)hello["boot_id"]!, (string)again["boot_id"]!);
    }

    [Fact]
    public async Task Pausing_and_resuming_send_a_data_changed_frame_for_the_pause_topic_to_an_open_stream()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var stream = await admin.OpenStreamAsync(Stream);
        await stream.NextEventNamedAsync("server.hello");

        var paused = await admin.PutWithCsrfAsync($"{WeirClient.Api}/pause", new JsonObject { ["paused"] = true });
        Assert.True(paused.Status == HttpStatusCode.OK, paused.ToString());
        var pausedFrame = await NextChangeOfAsync(stream, "pause");
        var resumed = await admin.PutWithCsrfAsync($"{WeirClient.Api}/pause", new JsonObject { ["paused"] = false });
        Assert.True(resumed.Status == HttpStatusCode.OK, resumed.ToString());
        var resumedFrame = await NextChangeOfAsync(stream, "pause");

        Assert.Equal(["topic"], pausedFrame.Select(field => field.Key));
        Assert.Equal("pause", (string)pausedFrame["topic"]!);
        Assert.Equal("pause", (string)resumedFrame["topic"]!);
    }

    [Fact]
    public async Task A_data_changed_frame_for_another_topic_just_before_a_pause_does_not_hide_the_pause_frame()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var stream = await admin.OpenStreamAsync(Stream);
        await stream.NextEventNamedAsync("server.hello");

        // Weir's own tasks announce readiness and the request counters at any moment while a stream is open. Queuing a
        // maintenance sweep puts two other topics on the stream, ahead of the pause, at a moment the test chooses.
        var queued = await admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/maintenance/run", new JsonObject { ["family"] = "work_temp_stale_sweep" });
        JobsApi.Expect(queued, HttpStatusCode.OK);
        var paused = await admin.PutWithCsrfAsync($"{WeirClient.Api}/pause", new JsonObject { ["paused"] = true });
        Assert.True(paused.Status == HttpStatusCode.OK, paused.ToString());
        var pausedFrame = await NextChangeOfAsync(stream, "pause");
        var resumed = await admin.PutWithCsrfAsync($"{WeirClient.Api}/pause", new JsonObject { ["paused"] = false });
        Assert.True(resumed.Status == HttpStatusCode.OK, resumed.ToString());
        var resumedFrame = await NextChangeOfAsync(stream, "pause");

        Assert.Equal("pause", (string)pausedFrame["topic"]!);
        Assert.Equal("pause", (string)resumedFrame["topic"]!);
    }

    [Fact]
    public async Task Queuing_a_file_pass_sends_data_changed_frames_for_the_job_queue_and_files_at_once()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Combine(folders.Path, "stream_watch")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(folders.Path, "stream_out")).FullName;
        using var admin = await Server.CreateAdminClientAsync();
        await JobsApi.SetMovieFoldersAsync(admin, watched, output);
        using var stream = await admin.OpenStreamAsync(Stream);
        await stream.NextEventNamedAsync("server.hello");

        var queued = await admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/jobs/file-remux-pass/enqueue", new JsonObject { ["relative_media_path"] = "movies/streamed.mkv" });
        JobsApi.Expect(queued, HttpStatusCode.OK);

        Assert.Equal(["files_at_once", "jobs"], await TopicsAnnouncedAsync(stream, "jobs", "files_at_once"));
    }

    [Fact]
    public async Task Queuing_a_maintenance_sweep_sends_data_changed_frames_for_the_job_queue_and_the_maintenance_panel()
    {
        using var admin = await Server.CreateAdminClientAsync();
        using var stream = await admin.OpenStreamAsync(Stream);
        await stream.NextEventNamedAsync("server.hello");

        var queued = await admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/maintenance/run", new JsonObject { ["family"] = "work_temp_stale_sweep" });
        JobsApi.Expect(queued, HttpStatusCode.OK);

        Assert.Equal(["jobs", "maintenance"], await TopicsAnnouncedAsync(stream, "jobs", "maintenance"));
    }

    [Fact]
    public async Task Record_activity_event_does_not_prune_history_using_log_retention()
    {
        const string title = "Old Processing result that still backs overview history";
        const int logRetentionDays = 1;
        await using var server = await WeirServer.StartNewAsync();
        using (var admin = await server.CreateAdminClientAsync())
        {
            var current = await ActivitySettings.CurrentAsync(admin);
            var saved = await ActivitySettings.SaveAsync(admin, ActivitySettings.UpdateBody(current, logRetentionDays: logRetentionDays));
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        }

        await using (var database = await server.StopForDatabaseAsync())
        {
            ActivityRows.InsertEvent(
                database.Connection,
                "processing.file_remux_pass_completed",
                "processing",
                title,
                detail: "{}",
                createdAt: DateTime.UtcNow - TimeSpan.FromDays(10),
                facts: new EventFacts(Result: "success"));
        }

        using var client = server.CreateClient();
        await client.LoginAsync(); // records a new sign-in event

        var response = await client.GetAsync(Recent, ("limit", 100));
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var items = response.Fields["items"]!.AsArray();
        Assert.Contains(title, items.Select(item => (string)item!["title"]!));
        Assert.Contains(items, item => (string)item!["module"]! == "auth");
    }

    /// <summary>
    /// The next <c>data.changed</c> frame for <paramref name="topic"/>. Weir announces readiness, its request counters and
    /// more on its own schedule while a stream is open, so the first frame after an action is not always the action's.
    /// </summary>
    private static async Task<JsonObject> NextChangeOfAsync(SseReader stream, string topic)
    {
        while (true)
        {
            var frame = (await stream.NextEventNamedAsync("data.changed")).AsObject();
            if ((string?)frame["topic"] == topic)
            {
                return frame;
            }
        }
    }

    /// <summary>The <c>data.changed</c> topics the stream sends until every one in <paramref name="expected"/> has come, sorted.</summary>
    private static async Task<string[]> TopicsAnnouncedAsync(SseReader stream, params string[] expected)
    {
        var heard = new SortedSet<string>(StringComparer.Ordinal);
        while (!expected.All(heard.Contains))
        {
            var frame = (await stream.NextEventNamedAsync("data.changed")).AsObject();
            heard.Add((string)frame["topic"]!);
        }

        return [.. heard.Where(expected.Contains)];
    }

    private static async Task<long> LatestActivityIdAsync(WeirClient client)
    {
        var response = await client.GetAsync(Recent, ("limit", 100));
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response.Fields["items"]!.AsArray().Max(item => (long)item!["id"]!);
    }
}
