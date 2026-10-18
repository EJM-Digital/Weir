using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// The connection test, the queue and the removal of a queue item, against what a real Sonarr and a real Radarr answered
/// (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>, recorded by <c>apps/server/tools/Weir.ArrRecorder</c>). Every reply is a
/// recorded one; each test also holds that Weir asked for exactly what the recording says it asked for.
/// </summary>
public sealed class ArrConnectionAndQueueRepliesTests
{
    public static TheoryData<string> Apps => ["sonarr", "radarr"];

    private static string ScopeOf(string app) => app == "sonarr" ? MediaManagerKinds.Tv : MediaManagerKinds.Movie;

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_connection_test_and_the_heartbeat_say_connected_when_the_app_answers_its_status(string app)
    {
        var recording = ArrRecording.Load(app, "connection-test");
        using var replay = new ArrReplay(app, recording);

        var (ok, detail) = await ManagerHealthProbe.ProbeAsync(
            replay, "the app", app, replay.Connection().BaseUrl, "a-key-the-recording-never-saw", TimeSpan.FromSeconds(10), connection: null, CancellationToken.None);

        Assert.Equal(200, recording.Only.Reply.Status);
        Assert.Equal(recording.Version, (string)recording.Only.Reply.Body!["version"]!);
        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach the app.", detail);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_connection_test_names_a_refused_key_without_the_apps_words(string app)
    {
        var recording = ArrRecording.Load(app, "connection-test-wrong-key");
        using var replay = new ArrReplay(app, recording);

        var (ok, detail) = await ManagerHealthProbe.ProbeAsync(
            replay, "the app", app, replay.Connection().BaseUrl, "a-key-the-recording-never-saw", TimeSpan.FromSeconds(10), connection: null, CancellationToken.None);

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.False(ok);
        Assert.Equal("Weir reached the app, but the API key was refused. Check the key and save it again.", detail);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_key_the_app_refuses_is_reported_in_plain_words_with_no_status_or_body(string app)
    {
        var recording = ArrRecording.Load(app, "describe-wrong-key");
        using var replay = new ArrReplay(app, recording);

        var description = await replay.Port().DescribeAsync(replay.Connection());

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.Equal(SignalStatus.Unreachable, description.Status);
        Assert.Equal(
            $"{replay.Connection().Label} refused Weir's API key, so it could not say which folders it manages. " +
            "Check the key in Setup › Connections › Media managers and save it again.",
            description.Detail);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_fresh_app_with_no_root_folder_answers_the_connection_test_with_nothing_to_manage(string app)
    {
        var recording = ArrRecording.Load(app, "describe-no-root-folder");
        using var replay = new ArrReplay(app, recording);

        var description = await replay.Port().DescribeAsync(replay.Connection());

        Assert.Equal(SignalStatus.Reported, description.Status);
        Assert.Empty(description.LibraryRoots);
        Assert.Empty(description.Libraries);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_connection_test_reads_each_root_folder_the_app_names(string app)
    {
        var recording = ArrRecording.Load(app, "describe");
        using var replay = new ArrReplay(app, recording);
        var recorded = recording.Only.Reply.Body!.AsArray().Select(folder => folder!.AsObject()).ToList();

        var description = await replay.Port().DescribeAsync(replay.Connection());

        Assert.Equal(SignalStatus.Reported, description.Status);
        Assert.Equal(recorded.Select(folder => (string)folder["path"]!), description.LibraryRoots);
        var library = Assert.Single(description.Libraries);
        Assert.Equal(recorded[0]["id"]!.ToJsonString(), library.Key);
        Assert.Equal(ScopeOf(app), library.MediaScope);
        Assert.Equal((string)recorded[0]["path"]!, library.RootPath);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task An_app_that_is_downloading_nothing_has_an_empty_queue(string app)
    {
        var recording = ArrRecording.Load(app, "queue-empty");
        using var replay = new ArrReplay(app, recording);

        var signal = await replay.Port().QueueRowsAsync(replay.Connection());

        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.Empty(signal.Rows);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_queued_download_is_read_as_one_row_that_is_still_busy_and_belongs_to_its_file_and_title(string app)
    {
        var recording = ArrRecording.Load(app, "queue-with-download");
        using var replay = new ArrReplay(app, recording);
        var recorded = recording.Only.Reply.Body!["records"]!.AsArray().Single()!.AsObject();
        var scope = ScopeOf(app);

        var signal = await replay.Port().QueueRowsAsync(replay.Connection());

        var row = Assert.Single(signal.Rows);
        Assert.Equal(scope, row.Scope);
        Assert.Equal((string)recorded["downloadId"]!, WireConvert.Str(row.Payload["downloadId"]));
        var byPath = ManagerQueueSignals.AttributedQueueRows([signal], scope, candidatePath: (string)recorded["outputPath"]!).Single().View;
        var entityId = (long)recorded[app == "sonarr" ? "seriesId" : "movieId"]!;
        var byTitle = ManagerQueueSignals.AttributedQueueRows([signal], scope, candidateEntityId: entityId).Single().View;
        var elsewhere = ManagerQueueSignals.AttributedQueueRows([signal], scope, candidatePath: "/somewhere/else.mkv", candidateEntityId: entityId + 1).Single().View;
        Assert.True(byPath.AppliesToFile);
        Assert.True(byTitle.AppliesToFile);
        Assert.False(elsewhere.AppliesToFile);
        Assert.True(byPath.IsUpstreamActive);
        Assert.False(byPath.IsImportPending);
        Assert.Equal((string)recorded["title"]!, byPath.QueueTitle);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Removing_a_queued_download_asks_the_app_to_drop_it_from_the_client_and_blocklist_the_release(string app)
    {
        var queue = ArrRecording.Load(app, "queue-with-download");
        var removal = ArrRecording.Load(app, "queue-remove-and-blocklist");
        using var replay = new ArrReplay(app, queue, removal);
        var port = replay.Port();
        var row = Assert.Single((await port.QueueRowsAsync(replay.Connection())).Rows);

        await port.RemoveQueueItemAsync(replay.Connection(), row.Payload);

        Assert.Equal(200, removal.Only.Reply.Status);
        Assert.Equal("DELETE", removal.Only.Request.Method);
        replay.AssertSentAsRecorded(removal);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Removing_a_queue_item_the_app_does_not_have_is_refused_by_the_app_and_says_so(string app)
    {
        var recording = ArrRecording.Load(app, "queue-remove-unknown-item");
        using var replay = new ArrReplay(app, recording);

        var refused = await Assert.ThrowsAsync<MediaManagerHttpException>(
            () => replay.Port().RemoveQueueItemAsync(replay.Connection(), new WireObject().Set("id", 987654L)));

        Assert.Equal(404, recording.Only.Reply.Status);
        Assert.Equal(404, refused.StatusCode);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Once_the_item_is_removed_the_queue_is_empty_again(string app)
    {
        var recording = ArrRecording.Load(app, "queue-after-remove");
        using var replay = new ArrReplay(app, recording);

        var signal = await replay.Port().QueueRowsAsync(replay.Connection());

        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.Empty(signal.Rows);
        replay.AssertSentAsRecorded(recording);
    }
}
