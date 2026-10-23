using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>What Weir does when it cannot record a hand-off's outcome, has no manager to tell, or cannot read the manager's libraries.</summary>
public sealed class HandoffReportRecordingTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Relative = "Film/film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly CapturingLogger<HandoffCompletionReporter> _log = new();

    public void Dispose() => _fixture.Dispose();

    private HandoffCompletionReporter Reporter(MediaManagerConnectionService? connections = null) =>
        new(connections ?? _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Libraries, _fixture.Http, _fixture.Store.Clock, _log);

    private static WireObject Finished(string extra = "") =>
        (WireObject)WireJsonParser.Parse($$"""{"ok":true,"outcome":"live_output_written","output_file":"/out/Film/film.mkv","relative_media_path":"{{Relative}}"{{extra}}}""");

    private static string Payload(string handoffId, string extra = "") =>
        $$$"""{"relative_media_path":"{{{Relative}}}","media_scope":"movie","origin":{"source_key":"deluno",{{{handoffId}}}"callback_path":"{{{Callback}}}"{{{extra}}}}}""";

    /// <summary>Deluno hands over the film; the payload of the job Weir queues for it is what a finished pass reports from.</summary>
    private async Task<string> ReceiveAsync(string handoffId)
    {
        var watched = _fixture.Store.Home.Join("watched");
        await _fixture.LibraryAsync("movie", watched);
        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(
            uow,
            new MediaManagerImportEvent
            {
                SourceKey = "deluno",
                EventKind = "handoff",
                MediaScope = "movie",
                FilePath = Path.Join(watched, "Film", "film.mkv"),
                HandoffId = handoffId,
                CallbackPath = Callback,
            }));
        return (await _fixture.Jobs.ListAsync()).Single().PayloadJson!;
    }

    private Task<string> ReportAsync(HandoffCompletionReporter reporter, string payload, WireObject result) =>
        _fixture.Db(uow => reporter.ReportHandoffCompletionAsync(uow, payload, result), commit: false);

    [Fact]
    public async Task A_report_with_no_hand_off_id_that_cannot_be_written_to_Activity_is_logged_and_not_recorded()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", null);
        _fixture.DelunoListsNoLibraries();
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        await _fixture.Store.Execute("ALTER TABLE activity_events RENAME TO activity_events_unavailable");

        var status = await ReportAsync(Reporter(), Payload(string.Empty), Finished());

        Assert.StartsWith("reported completed to ", status, StringComparison.Ordinal);
        Assert.True(_log.Logged(LogLevel.Warning, "Could not record the hand-off outcome."));
        Assert.False(_fixture.Http.RequestsTo(HttpMethod.Post, Callback).Single().Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task A_hand_off_the_ledger_never_heard_of_is_reported_and_waits_when_the_manager_is_busy()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099");
        _fixture.DelunoListsNoLibraries();
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable));
        var result = (WireObject)WireJsonParser.Parse("""{"ok":false,"outcome":"failed","error":"ffmpeg stopped"}""");

        var status = await ReportAsync(Reporter(), $$$"""{"origin":{"source_key":"deluno","handoff_id":"ghost","callback_path":"{{{Callback}}}"}}""", result);

        Assert.Equal("failed: Deluno on 192.0.2.30 answered HTTP 503", status);
        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs"));
    }

    [Fact]
    public async Task A_claim_that_cannot_be_saved_as_owed_is_rolled_back_and_nothing_is_sent()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.DelunoListsNoLibraries();
        var payload = await ReceiveAsync("h1");
        await _fixture.Store.Execute(
            "CREATE TRIGGER refuse_owed BEFORE UPDATE OF pending_report_json ON media_manager_handoffs BEGIN SELECT RAISE(ABORT, 'no room'); END");

        var status = await ReportAsync(Reporter(), payload, Finished());

        Assert.Equal("skipped: the outcome could not be recorded", status);
        Assert.True(_log.Logged(LogLevel.Warning, "Could not record the hand-off outcome."));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
    }

    [Fact]
    public async Task A_finished_hand_off_with_no_enabled_manager_is_recorded_and_nothing_is_owed()
    {
        var payload = await ReceiveAsync("h1");

        var status = await ReportAsync(Reporter(), payload, Finished());

        Assert.Equal("skipped: no enabled deluno connection is configured to report back to", status);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
        Assert.Equal("completed", await _fixture.Store.ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }

    [Fact]
    public async Task A_manager_whose_libraries_cannot_be_read_at_all_still_gets_the_report_with_the_local_path()
    {
        var connections = new MediaManagerConnectionService(_fixture.Store.Options, _fixture.Cipher, new PortsThatCannotDescribe(), _fixture.ConnectionStore);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var result = Finished(""","processing_output_folder_resolved":"/out" """);

        var status = await ReportAsync(Reporter(connections), Payload(string.Empty, ",\"library_id\":\"lib-1\""), result);

        Assert.StartsWith("reported completed to ", status, StringComparison.Ordinal);
        Assert.True(_log.Logged(LogLevel.Warning, "to place the output path"));
        var body = (WireObject)_fixture.Http.RequestsTo(HttpMethod.Post, Callback).Single().Json!;
        Assert.Equal("/out/Film/film.mkv", ((WireString)body["outputPath"]).Value);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_status_code_Weir_knows_is_a_report_the_manager_did_not_take()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response((HttpStatusCode)150));
        var origin = HandoffOrigin.FromPayload(WireJsonParser.Parse(Payload(string.Empty)))!;
        var reporter = Reporter();
        var (target, _) = await _fixture.Db(uow => reporter.ResolveHandoffTargetAsync(uow, origin), commit: false);

        var delivery = await reporter.PostHandoffReportAsync(target!, Finished());

        Assert.False(delivery.Accepted);
        Assert.EndsWith("answered HTTP 150", delivery.Status, StringComparison.Ordinal);
        Assert.False(HandoffCompletionReporter.IsTransientFailure(delivery));
    }

    [Fact]
    public async Task A_result_that_names_no_folder_of_its_own_is_reported_with_the_local_path_without_asking_the_manager_anything()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.OK));

        var status = await ReportAsync(Reporter(), Payload(string.Empty, ",\"library_id\":\"lib-1\""), Finished());

        Assert.StartsWith("reported completed to ", status, StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Get, "/api/integrations/external/manifest"));
        var body = (WireObject)_fixture.Http.RequestsTo(HttpMethod.Post, Callback).Single().Json!;
        Assert.Equal("/out/Film/film.mkv", ((WireString)body["outputPath"]).Value);
    }

    private Task<int> RecordAsync(HandoffReportTarget target, string body, HandoffReportDelivery delivery, string? relativePath) =>
        _fixture.Db(async uow =>
        {
            await HandoffCompletionReporter.RecordHandoffReportAsync(uow, target, (WireObject)WireJsonParser.Parse(body), delivery, relativePath);
            return 0;
        });

    public static TheoryData<string, bool, bool, string> Titles => new()
    {
        { """{"status":"completed"}""", false, true, "{0} had already stopped waiting for film.mkv" },
        { """{"status":"completed"}""", false, false, "Weir could not tell {0} about film.mkv" },
        { """{"status":"completed","disposition":"rejected"}""", true, false, "Told {0} that film.mkv is a bad release and was removed" },
        { """{"status":"completed"}""", true, false, "Told {0} that film.mkv is ready to import" },
        { $$"""{"status":"failed","failureClass":"{{CompletionReports.SourceGoneFailureClass}}"}""", true, false, "Told {0} that film.mkv is no longer there" },
        { $$"""{"status":"failed","failureClass":"{{CompletionReports.NotTakenFailureClass}}"}""", true, false, "Told {0} that Weir did not start film.mkv, because it is working on it for another connection" },
        { $$"""{"status":"failed","failureClass":"{{CompletionReports.SupersededFailureClass}}"}""", true, false, "Told {0} that its newer send of film.mkv has the answer" },
        { """{"status":"failed"}""", true, false, "Told {0} that Weir could not process film.mkv" },
    };

    [Theory]
    [MemberData(nameof(Titles))]
    public async Task A_report_is_titled_by_what_the_manager_was_told_and_whether_it_was_listening(string body, bool accepted, bool stopped, string title)
    {
        var target = new HandoffReportTarget(new ManagerConnection("deluno", "Deluno", "http://192.0.2.30:5099", "k1", 1), $"http://192.0.2.30:5099{Callback}", new Dictionary<string, string>());
        var delivery = new HandoffReportDelivery(accepted, "delivered", stopped);

        await RecordAsync(target, body, delivery, Relative);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, title, target.Connection.Label), await _fixture.Store.ScalarText("SELECT title FROM activity_events"));
    }

    [Fact]
    public async Task A_report_with_no_file_is_titled_by_the_release_or_as_a_handed_over_file()
    {
        var target = new HandoffReportTarget(new ManagerConnection("deluno", "Deluno", "http://192.0.2.30:5099", "k1", 1), $"http://192.0.2.30:5099{Callback}", new Dictionary<string, string>());
        var delivery = new HandoffReportDelivery(true, "delivered");

        await RecordAsync(target, """{"status":"completed","releaseName":"Film.2026"}""", delivery, null);
        await RecordAsync(target, """{"status":"completed"}""", delivery, string.Empty);

        var label = target.Connection.Label;
        Assert.Equal(
            [$"Told {label} that Film.2026 is ready to import", $"Told {label} that a handed-over file is ready to import"],
            await _fixture.Db(uow => uow.QueryAsync("SELECT title FROM activity_events ORDER BY id", reader => reader.GetString(0))));
    }

    private sealed class PortsThatCannotDescribe : IMediaManagerPorts
    {
        public IMediaManagerPort? PortForKind(string? kind) => new Port(kind!);

        private sealed class Port(string kind) : IMediaManagerPort
        {
            public string Kind => kind;

            public ManagerCapabilities Capabilities() => throw new NotSupportedException();

            public Task<ManagerDescription> DescribeAsync(ManagerConnection connection, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("The manager's answer made no sense.");

            public Task<ManagerQueueSignal> QueueRowsAsync(ManagerConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task RemoveQueueItemAsync(ManagerConnection connection, WireObject row, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<ManagerLibraryTruth> LibraryTruthAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<ManagerLibraryFilesSignal> ListLibraryFilesAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<ManagerNotifyOutcome> FileChangedAsync(
                ManagerConnection connection,
                IReadOnlySet<string> advertisedCapabilities,
                string? titleId,
                string filePath,
                string? reason,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
