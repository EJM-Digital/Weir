using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// <see cref="ProcessingRejectHandler"/> against a real
/// database and real temporary folders, with each manager behind <see cref="FakeManagerHttp"/>.
/// </summary>
public sealed class ProcessingRejectHandlerTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string ManifestPath = "/api/integrations/external/manifest";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private ProcessingRejectHandler Handler() =>
        new(
            _fixture.Store.Database,
            _fixture.Ports,
            _fixture.Connections,
            _fixture.Reporter,
            new RejectRoutes(_fixture.Ports, _fixture.Reporter),
            _fixture.Ledger,
            _fixture.Jobs,
            new RejectPacing(TimeProvider.System),
            _fixture.Libraries,
            TimeProvider.System,
            NullLogger<ProcessingRejectHandler>.Instance);

    private async Task<long> LibraryAsync(string rejectedFileAction = "leave")
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, rejected_file_action, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, 'reject', $action, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$action", rejectedFileAction))), CultureInfo.InvariantCulture);
    }

    private Task LinkAsync(long libraryId, long connectionId) =>
        _fixture.Store.Execute($"INSERT INTO library_manager_links (library_id, connection_id) VALUES ({libraryId}, {connectionId})");

    private Task FileRowAsync(long libraryId, string relativePath, string status = "processing_failed") =>
        _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({libraryId}, '{relativePath}', '{status}', 'ffmpeg could not read the audio track.')");

    private static JobWorkContext Context(long id, string payload, int attempt = 1, int maxAttempts = 1) =>
        new(id, IntakeRules.RejectJobKind, payload, "test", attempt, maxAttempts);

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // --- through a hand-off (Deluno) --------------------------------------------------------------

    [Fact]
    public async Task An_accepted_handoff_rejection_leaves_a_linked_workflows_download_for_the_manager_and_reports_it_not_removed()
    {
        var library = await LibraryAsync(rejectedFileAction: "delete_file");
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"ffmpeg could not read the audio track.","failure_class":"execution","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-movies","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(41, payload), CancellationToken.None);

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("rejected", WireConvert.Str(body["disposition"]));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.Equal("lib-movies", WireConvert.Str(body["libraryId"]));
        Assert.Equal("execution", WireConvert.Str(body["failureClass"]));
        Assert.True(File.Exists(source), "the download is the manager's and its client's to remove");
        Assert.Equal("rejected", await ScalarText("SELECT status FROM files"));
        Assert.Contains(
            "Deluno on 192.0.2.30 will remove the download; Weir left it in place.",
            await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_rejected'"),
            StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_rejected'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_reported'"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
        Assert.Equal("rejected", await ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }

    [Fact]
    public async Task A_rejection_the_manager_accepted_is_reported_once_however_often_the_job_runs()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"ffmpeg could not read the audio track.","origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(46, payload), CancellationToken.None);
        await Handler().HandleAsync(Context(46, payload), CancellationToken.None);

        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        Assert.Equal("rejected", await ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handoff_reported'"));
    }

    [Fact]
    public async Task A_rejection_the_manager_accepted_before_the_rest_could_be_saved_is_finished_without_reporting_it_again()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET reported_status = 'rejected' WHERE handoff_id = 'h1'");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"ffmpeg could not read the audio track.","origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(47, payload), CancellationToken.None);

        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        Assert.True(File.Exists(source));
        Assert.Equal("rejected", await ScalarText("SELECT status FROM files"));
        Assert.Equal("rejected", await ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_rejected'"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_rejection_the_manager_accepted_is_noted_on_the_handoff_before_anything_else_is_saved()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(48, payload), CancellationToken.None);

        Assert.Equal("rejected", await ScalarText("SELECT reported_status FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }

    [Fact]
    public async Task An_accepted_handoff_rejection_removes_the_source_of_a_workflow_no_manager_is_linked_to()
    {
        var library = await LibraryAsync(rejectedFileAction: "delete_file");
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"ffmpeg could not read the audio track.","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-movies","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(42, payload), CancellationToken.None);

        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath)).Json!;
        Assert.True(((WireBool)body["sourceRemoved"]).Value);
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task A_refused_rejection_keeps_the_download_and_hands_it_back()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var bytesBefore = File.ReadAllBytes(source);
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Conflict);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"ffmpeg could not read the audio track.","origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(42, payload), CancellationToken.None);

        Assert.Equal(bytesBefore, File.ReadAllBytes(source));
        var reason = await ScalarText("SELECT status_reason FROM files");
        Assert.NotEqual("rejected", await ScalarText("SELECT status FROM files"));
        Assert.Contains("did not accept the rejection", reason, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_reject_fell_back'"));
    }

    [Fact]
    public async Task An_unreachable_manager_is_not_an_acceptance()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var bytesBefore = File.ReadAllBytes(source);
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Throw(HttpMethod.Post, EventsPath, new HttpRequestException("no route to host"));
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(43, payload), CancellationToken.None);

        Assert.Equal(bytesBefore, File.ReadAllBytes(source));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_is_asked_again_before_the_original_is_handed_back()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var bytesBefore = File.ReadAllBytes(source);
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        _fixture.Http.Throw(HttpMethod.Get, ManifestPath, new HttpRequestException("no route to host"));
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(Context(44, payload, attempt: 1, maxAttempts: 3), CancellationToken.None));

        Assert.Equal(bytesBefore, File.ReadAllBytes(source));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_reject_fell_back'"));
    }

    [Fact]
    public async Task A_manager_that_still_does_not_answer_on_the_last_attempt_has_the_original_handed_back()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        _fixture.Http.Throw(HttpMethod.Get, ManifestPath, new HttpRequestException("no route to host"));
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(45, payload, attempt: 3, maxAttempts: 3), CancellationToken.None);

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_manager_that_cannot_replace_the_release_is_never_told_to_reject()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":[],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(44, payload), CancellationToken.None);

        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        Assert.Contains("does not yet say it can replace a rejected release", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    // --- through Sonarr / Radarr's queue -----------------------------------------------------------

    [Fact]
    public async Task A_single_file_download_is_removed_and_blocklisted_through_the_queue()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        var outputPath = Path.GetFullPath(source).Replace('\\', '/');
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", $$"""{"records":[{"id":55,"outputPath":"{{outputPath}}","downloadId":"dl1"}]}""");
        _fixture.Http.Route(HttpMethod.Delete, "/api/v3/queue/55", _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var payload = $$"""{"relative_media_path":"Film/film.mkv","library_id":{{library}}}""";

        await Handler().HandleAsync(Context(45, payload), CancellationToken.None);

        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
        Assert.Equal("rejected", await ScalarText("SELECT status FROM files"));
        Assert.Contains("removed the download and blocklisted the release", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_matching_queue_item_hands_the_original_back()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", """{"records":[]}""");
        var payload = $$"""{"relative_media_path":"Film/film.mkv","library_id":{{library}}}""";

        await Handler().HandleAsync(Context(46, payload), CancellationToken.None);

        Assert.Contains("No download in the linked media manager's queue", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task Two_matching_queue_items_are_never_guessed_between()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        var outputPath = Path.GetFullPath(source).Replace('\\', '/');
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", $$"""{"records":[{"id":55,"outputPath":"{{outputPath}}"},{"id":56,"outputPath":"{{outputPath}}"}]}""");
        var payload = $$"""{"relative_media_path":"Film/film.mkv","library_id":{{library}}}""";

        await Handler().HandleAsync(Context(47, payload), CancellationToken.None);

        Assert.Contains("Weir could not tell which one to reject", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
    }

    [Fact]
    public async Task A_season_pack_tracked_as_several_items_is_never_rejected()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        var outputPath = Path.GetFullPath(source).Replace('\\', '/');
        _fixture.Http.Json(
            HttpMethod.Get, "/api/v3/queue",
            $$"""{"records":[{"id":55,"outputPath":"{{outputPath}}","downloadId":"dl1"},{"id":56,"outputPath":"other","downloadId":"dl1"}]}""");
        var payload = $$"""{"relative_media_path":"Film/film.mkv","library_id":{{library}}}""";

        await Handler().HandleAsync(Context(48, payload), CancellationToken.None);

        Assert.Contains("a season pack or", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
    }

    [Fact]
    public async Task No_linked_manager_that_can_reject_hands_the_original_back()
    {
        var library = await LibraryAsync();
        _folders.Source("solo.mkv");
        await FileRowAsync(library, "solo.mkv");
        var payload = $$"""{"relative_media_path":"solo.mkv","library_id":{{library}}}""";

        await Handler().HandleAsync(Context(49, payload), CancellationToken.None);

        Assert.Contains("No linked media manager can take a rejection", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_handoff_that_named_no_callback_path_cannot_be_reported_to_and_the_original_is_handed_back()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1"}}""";

        await Handler().HandleAsync(Context(50, payload), CancellationToken.None);

        Assert.Contains("Weir could not report to the manager: the hand-off named no callback path.", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_file_a_queue_manager_handed_over_is_rejected_through_its_queue()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        var outputPath = Path.GetFullPath(source).Replace('\\', '/');
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", $$"""{"records":[{"id":55,"outputPath":"{{outputPath}}","downloadId":"dl1"}]}""");
        _fixture.Http.Route(HttpMethod.Delete, "/api/v3/queue/55", _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"radarr","handoff_id":"h1","callback_path":"/unused"}}""";

        await Handler().HandleAsync(Context(51, payload), CancellationToken.None);

        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
        Assert.Equal("rejected", await ScalarText("SELECT status FROM files"));
    }

    [Fact]
    public async Task A_file_a_queue_manager_handed_over_is_handed_back_when_no_linked_manager_can_reject_it()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"radarr","handoff_id":"h1","callback_path":"/unused"}}""";

        await Handler().HandleAsync(Context(52, payload), CancellationToken.None);

        Assert.Contains("No linked media manager can take a rejection", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}'"));
    }

    [Fact]
    public async Task A_rejection_a_queue_manager_already_took_is_finished_without_naming_a_connection()
    {
        var library = await LibraryAsync();
        _folders.Source("Film/film.mkv");
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "radarr", "h1", library, "Film/film.mkv"); return 0; });
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET reported_status = 'rejected' WHERE handoff_id = 'h1'");
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"radarr","handoff_id":"h1","callback_path":"/unused"}}""";

        await Handler().HandleAsync(Context(53, payload), CancellationToken.None);

        Assert.Equal("rejected", await ScalarText("SELECT status FROM files"));
    }

    // --- fix #532: a rejection always upserts a Files row ----------------------------------------

    [Fact]
    public async Task A_rejection_upserts_a_files_row_even_with_no_prior_scan()
    {
        var library = await LibraryAsync(rejectedFileAction: "delete_file");
        _folders.Source("Film/film.mkv");
        // Deliberately no files row: this hand-off was never seen by a watched-folder scan.
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h9", library, "Film/film.mkv"); return 0; });
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"reason":"Weir could not read this file's contents.","failure_class":"preflight","origin":{"source_key":"deluno","handoff_id":"h9","callback_path":"{{{EventsPath}}}"}}""";

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));

        await Handler().HandleAsync(Context(50, payload), CancellationToken.None);

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal(
            "rejected|preflight",
            await ScalarText("SELECT status || '|' || failure_class FROM files WHERE relative_path = 'Film/film.mkv'"));
    }
}
