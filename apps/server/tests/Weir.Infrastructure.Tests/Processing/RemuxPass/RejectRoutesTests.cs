using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The two ways to ask a manager to take back a bad release, with the real ports over scripted manager replies and real
/// folders: what makes a queue item safe to remove, and what each kind of answer to a hand-off report comes to.
/// </summary>
public sealed class RejectRoutesTests : IDisposable
{
    private const string QueuePath = "/api/v3/queue";
    private const string ManifestPath = "/api/integrations/external/manifest";
    private const string EventsPath = "/api/integrations/processors/events";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private RejectRoutes Routes() => new(_fixture.Ports, _fixture.Reporter);

    private static ManagerConnection Radarr(string name = "Radarr") => new("radarr", name, "http://radarr.local", "key", 1);

    private static ManagerConnection Deluno() => new("deluno", "Deluno", "http://deluno.local", "key", 2);

    private static string Slashed(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private void Queue(string recordsJson) => _fixture.Http.Json(HttpMethod.Get, QueuePath, "{\"records\":" + recordsJson + "}");

    private static string Record(int id, string outputPath, string? downloadId = null) =>
        "{\"id\":" + id + ",\"outputPath\":\"" + outputPath + "\"" + (downloadId is null ? string.Empty : ",\"downloadId\":\"" + downloadId + "\"") + "}";

    private static HandoffReportTarget Target(ManagerConnection connection) =>
        new(connection, "http://deluno.local" + EventsPath, new Dictionary<string, string>());

    private static HandoffOrigin Origin() => new("deluno", "h1", EventsPath, null);

    private void DelunoCanReject() =>
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");

    private Task<RejectRouteOutcome> HandoffAsync(string source, WorkflowManagerLinks links, string? failureClass = "execution", string? watchedRoot = null) =>
        Routes().ThroughHandoffAsync(Target(Deluno()), Origin(), source, watchedRoot ?? _folders.Watched, "ffmpeg could not read the audio track.", failureClass, links, CancellationToken.None);

    [Fact]
    public void The_routes_cannot_be_built_without_their_parts()
    {
        Assert.Throws<ArgumentNullException>(() => new RejectRoutes(null!, _fixture.Reporter));
        Assert.Throws<ArgumentNullException>(() => new RejectRoutes(_fixture.Ports, null!));
    }

    [Fact]
    public async Task Every_entry_point_refuses_a_missing_argument()
    {
        var routes = Routes();
        var target = Target(Deluno());

        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.ThroughQueueAsync(null!, "s", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.ThroughQueueAsync([], null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.MatchQueueItemAsync(null!, "s", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.MatchQueueItemAsync([], null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.ThroughHandoffAsync(null!, Origin(), "s", "w", "r", null, WorkflowManagerLinks.None, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.ThroughHandoffAsync(target, null!, "s", "w", "r", null, WorkflowManagerLinks.None, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => routes.ThroughHandoffAsync(target, Origin(), "s", "w", "r", null, null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_single_file_download_is_removed_with_the_release_blocklisted_and_the_client_told()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source), "dl1") + "]");
        _fixture.Http.Route(HttpMethod.Delete, QueuePath + "/55", _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var radarr = Radarr();

        var outcome = await Routes().ThroughQueueAsync([radarr], source, CancellationToken.None);

        Assert.True(outcome.Done);
        Assert.False(outcome.Transient);
        Assert.Equal(radarr.Label, outcome.Manager);
        Assert.Contains("removed the download and blocklisted the release", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("queue", WireConvert.Str(outcome.DetailOrEmpty["route"]));
        Assert.Equal(55, ((WireInteger)outcome.DetailOrEmpty["queue_item"]!).Value);
        Assert.Equal("dl1", WireConvert.Str(outcome.DetailOrEmpty["download_id"]));
        var delete = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Delete, QueuePath + "/55"));
        Assert.Contains("removeFromClient=true", delete.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("blocklist=true", delete.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_when_asked_to_remove_the_item_is_asked_again_later()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source)) + "]");
        _fixture.Http.Throw(HttpMethod.Delete, QueuePath + "/55", new HttpRequestException("Connection refused."));

        var outcome = await Routes().ThroughQueueAsync([Radarr()], source, CancellationToken.None);

        Assert.False(outcome.Done);
        Assert.True(outcome.Transient);
        Assert.EndsWith("did not accept the rejection, so nothing was removed.", outcome.Reason, StringComparison.Ordinal);
        Assert.NotNull(outcome.DetailOrEmpty["technical_detail"]);
    }

    [Fact]
    public async Task A_manager_that_refuses_to_remove_the_item_keeps_it()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source)) + "]");
        _fixture.Http.Route(HttpMethod.Delete, QueuePath + "/55", _ => FakeManagerHttp.Response(HttpStatusCode.InternalServerError));

        var outcome = await Routes().ThroughQueueAsync([Radarr()], source, CancellationToken.None);

        Assert.False(outcome.Done);
        Assert.True(outcome.Transient);
        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Delete, QueuePath + "/55"));
    }

    [Fact]
    public async Task A_queue_that_does_not_answer_is_said_to_be_not_answering()
    {
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Throw(HttpMethod.Get, QueuePath, new HttpRequestException("Connection refused."));
        var radarr = Radarr();

        var match = await Routes().MatchQueueItemAsync([radarr], source, CancellationToken.None);

        Assert.False(match.Found);
        Assert.Equal($"{radarr.Label} is not answering, so Weir could not reject this release.", match.RefusalReason);
        Assert.Equal(radarr.Label, match.RefusalManager);
        Assert.True(match.RefusalTransient);
    }

    [Fact]
    public async Task A_connection_of_a_kind_with_no_port_is_passed_over_and_the_next_one_decides()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source)) + "]");
        var unknown = new ManagerConnection("plex", "Plex", "http://plex.local", "key", 3);

        var match = await Routes().MatchQueueItemAsync([unknown, Radarr()], source, CancellationToken.None);

        Assert.True(match.Found);
        Assert.Equal("radarr", match.Connection!.Kind);
    }

    [Fact]
    public async Task Rows_without_an_output_path_and_rows_for_other_files_do_not_match()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[{\"id\":1},{\"id\":3,\"outputPath\":\"/\"},{\"id\":2,\"outputPath\":\"" + Slashed(_folders.Source("Other/other.mkv")) + "\"}]");

        var match = await Routes().MatchQueueItemAsync([Radarr()], source, CancellationToken.None);

        Assert.False(match.Found);
        Assert.Equal("No download in the linked media manager's queue points at this file, so Weir could not reject it safely.", match.RefusalReason);
        Assert.Null(match.RefusalManager);
        Assert.False(match.RefusalTransient);
    }

    [Fact]
    public async Task Two_connections_each_holding_the_file_are_never_guessed_between()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source)) + "]");

        var match = await Routes().MatchQueueItemAsync([Radarr("One"), Radarr("Two")], source, CancellationToken.None);

        Assert.False(match.Found);
        Assert.StartsWith("More than one download in the queue points at this file", match.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Differences_of_case_and_slash_direction_still_match_the_same_file()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(source).ToUpperInvariant()) + "]");

        var match = await Routes().MatchQueueItemAsync([Radarr()], source.Replace('/', Path.DirectorySeparatorChar), CancellationToken.None);

        Assert.True(match.Found);
    }

    [Fact]
    public async Task A_download_folder_holding_only_this_video_is_the_download_of_this_file()
    {
        var source = _folders.Source("Film/film.mkv");
        _folders.Source("Film/film.nfo", 10);
        Queue("[" + Record(55, Slashed(Path.GetDirectoryName(source)!)) + "]");

        var match = await Routes().MatchQueueItemAsync([Radarr()], source, CancellationToken.None);

        Assert.True(match.Found);
    }

    [Fact]
    public async Task A_download_folder_holding_another_video_too_is_never_rejected()
    {
        var source = _folders.Source("Film/film.mkv");
        _folders.Source("Film/extra.mkv", 10);
        Queue("[" + Record(55, Slashed(Path.GetDirectoryName(source)!)) + "]");
        var radarr = Radarr();

        var match = await Routes().MatchQueueItemAsync([radarr], source, CancellationToken.None);

        Assert.False(match.Found);
        Assert.StartsWith("The download holds more than this one video file.", match.RefusalReason, StringComparison.Ordinal);
        Assert.Equal(radarr.Label, match.RefusalManager);
    }

    [Fact]
    public async Task A_download_folder_whose_only_video_is_a_different_file_is_never_rejected()
    {
        var source = Path.Join(_folders.Watched, "Film", "film.mkv");
        _folders.Source("Film/other.mkv", 10);
        Queue("[" + Record(55, Slashed(Path.GetDirectoryName(source)!)) + "]");

        var match = await Routes().MatchQueueItemAsync([Radarr()], source, CancellationToken.None);

        Assert.False(match.Found);
    }

    [Fact]
    public async Task A_download_folder_that_is_not_on_disk_is_never_rejected()
    {
        var source = Path.Join(_folders.Watched, "Film", "film.mkv");
        Queue("[" + Record(55, Slashed(Path.GetDirectoryName(source)!)) + "]");

        var match = await Routes().MatchQueueItemAsync([Radarr()], source, CancellationToken.None);

        Assert.False(match.Found);
    }

    [Fact]
    public async Task A_download_folder_that_cannot_be_listed_is_never_rejected()
    {
        var source = _folders.Source("Film/film.mkv");
        Queue("[" + Record(55, Slashed(Path.GetDirectoryName(source)!)) + "]");
        using var locked = UnreadablePath.Create(Path.GetDirectoryName(source)!);
        if (locked is null)
        {
            return;
        }

        var match = await Routes().MatchQueueItemAsync([Radarr()], source, CancellationToken.None);

        Assert.False(match.Found);
    }

    [Fact]
    public async Task A_connection_of_a_kind_with_no_port_cannot_be_asked_what_it_can_do()
    {
        var unknown = new ManagerConnection("plex", "Plex", "http://plex.local", "key", 3);

        var outcome = await Routes().ThroughHandoffAsync(Target(unknown), Origin(), "s", "w", "r", null, WorkflowManagerLinks.None, CancellationToken.None);

        Assert.False(outcome.Done);
        Assert.Equal($"Weir does not know how to ask {unknown.Label} what it can do.", outcome.Reason);
        Assert.Equal(unknown.Label, outcome.Manager);
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_the_question_is_said_to_be_not_answering()
    {
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Throw(HttpMethod.Get, ManifestPath, new HttpRequestException("Connection refused."));

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None);

        Assert.False(outcome.Done);
        Assert.True(outcome.Transient);
        Assert.Equal($"{Deluno().Label} is not answering, so Weir could not reject this release.", outcome.Reason);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Theory]
    [InlineData("""{"libraries":[]}""")]
    [InlineData("""{"capabilities":["something-else"],"libraries":[]}""")]
    public async Task A_manager_that_does_not_say_it_can_replace_a_rejected_release_is_not_told_to_reject(string manifest)
    {
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, manifest);

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None);

        Assert.False(outcome.Done);
        Assert.Equal($"{Deluno().Label} does not yet say it can replace a rejected release, so Weir did not delete the download.", outcome.Reason);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task A_source_that_is_already_gone_has_nothing_to_reject()
    {
        DelunoCanReject();
        var gone = Path.Join(_folders.Watched, "Film", "film.mkv");

        var outcome = await HandoffAsync(gone, WorkflowManagerLinks.None);

        Assert.False(outcome.Done);
        Assert.Equal($"The original is no longer at {gone}, so there is nothing to reject.", outcome.Reason);
        Assert.Null(outcome.Manager);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_the_report_keeps_the_download_and_is_asked_again()
    {
        DelunoCanReject();
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Throw(HttpMethod.Post, EventsPath, new HttpRequestException("Connection refused."));

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None);

        Assert.False(outcome.Done);
        Assert.True(outcome.Transient);
        Assert.Equal($"{Deluno().Label} is not answering, so Weir could not reject this release. Weir kept the download.", outcome.Reason);
        Assert.False(outcome.Report!.Delivery.Accepted);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task A_manager_that_refuses_the_report_keeps_the_download_and_says_what_it_answered(HttpStatusCode status, bool transient)
    {
        DelunoCanReject();
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", status);

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None);

        Assert.False(outcome.Done);
        Assert.Equal(transient, outcome.Transient);
        Assert.Equal($"{Deluno().Label} did not accept the rejection ({Deluno().Label} answered HTTP {(int)status}), so Weir kept the download.", outcome.Reason);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task An_accepted_report_for_a_linked_workflow_leaves_the_download_for_the_manager()
    {
        DelunoCanReject();
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);

        var outcome = await HandoffAsync(source, new WorkflowManagerLinks(["deluno"]), failureClass: null);

        Assert.True(outcome.Done);
        Assert.Contains("will remove the download; Weir left it in place.", outcome.Reason, StringComparison.Ordinal);
        Assert.False(((WireBool)outcome.DetailOrEmpty["source_removed"]!).Value);
        Assert.True(File.Exists(source));
        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath)).Json!;
        Assert.False(body.ContainsKey("failureClass"));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
    }

    [Fact]
    public async Task An_accepted_report_for_an_unlinked_workflow_removes_the_source_and_names_the_failure_class()
    {
        DelunoCanReject();
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None, failureClass: "preflight");

        Assert.True(outcome.Done);
        Assert.StartsWith($"{Deluno().Label} accepted that this release is bad and can find a different one.", outcome.Reason, StringComparison.Ordinal);
        Assert.True(((WireBool)outcome.DetailOrEmpty["source_removed"]!).Value);
        Assert.False(File.Exists(source));
        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath)).Json!;
        Assert.Equal("preflight", WireConvert.Str(body["failureClass"]));
        Assert.True(((WireBool)body["sourceRemoved"]).Value);
    }

    [Fact]
    public async Task An_accepted_report_whose_source_cannot_be_removed_tells_the_operator_to_remove_it_by_hand()
    {
        DelunoCanReject();
        var source = _folders.Source("Film/film.mkv");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var elsewhere = Directory.CreateDirectory(Path.Join(_folders.Root, "elsewhere")).FullName;

        var outcome = await HandoffAsync(source, WorkflowManagerLinks.None, watchedRoot: elsewhere);

        Assert.True(outcome.Done);
        Assert.Contains("accepted that this release is bad, but Weir could not remove the download:", outcome.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Remove it by hand; Weir will not process it again.", outcome.Reason, StringComparison.Ordinal);
        Assert.False(((WireBool)outcome.DetailOrEmpty["source_removed"]!).Value);
        Assert.True(File.Exists(source));
    }
}
