using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// <see cref="ProcessingPassThroughHandler"/> against a
/// real database and real temporary folders, with the manager side behind <see cref="FakeManagerHttp"/>.
/// </summary>
public sealed class ProcessingPassThroughHandlerTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private ProcessingPassThroughHandler Handler() =>
        new(_fixture.Store.Database, TimeProvider.System, NullLogger<ProcessingPassThroughHandler>.Instance, _fixture.Handback, _fixture.Libraries, _fixture.Jobs, _fixture.Reporter)
        {
            GoneSettle = TimeSpan.Zero,
        };

    private async Task<long> LibraryAsync(string collision = "replace")
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, output_collision_policy, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, $collision, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$collision", collision))), CultureInfo.InvariantCulture);
    }

    private Task FileRowAsync(long libraryId, string relativePath, string status = "processing_failed") =>
        _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({libraryId}, '{relativePath}', '{status}', 'failed')");

    private static JobWorkContext Context(long id, string payload) => new(id, IntakeRules.PassThroughJobKind, payload, "test");

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task The_original_is_delivered_byte_for_byte_and_the_source_survives()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Path.Join("Movie", "file.mkv"), bytes: 5000);
        await FileRowAsync(library, "Movie/file.mkv");
        var payload = $$"""{"relative_media_path":"Movie/file.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Handler().HandleAsync(Context(1, payload), CancellationToken.None);

        var destination = _folders.Out(Path.Join("Movie", "file.mkv"));
        Assert.True(File.Exists(destination));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        Assert.True(File.Exists(source), "the source must never be deleted by a pass-through");
        Assert.Equal("passed_through", await ScalarText("SELECT status FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_passed_through'"));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_passed_through'"));
        Assert.True(((WireBool)detail["delivered"]).Value);
        Assert.True(((WireBool)detail["source_kept"]).Value);
    }

    [Fact]
    public async Task A_file_deleted_before_it_could_be_handed_back_settles_as_nothing_to_do()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "gone.mkv");
        var payload = $$"""{"relative_media_path":"gone.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Handler().HandleAsync(Context(2, payload), CancellationToken.None);

        Assert.False(File.Exists(_folders.Out("gone.mkv")));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_pass_through_failed'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
        Assert.Equal("skipped", await ScalarText("SELECT result FROM activity_events"));
        Assert.Equal("gone.mkv is no longer there, so there is nothing to do", await ScalarText("SELECT title FROM activity_events"));

        // Held, not forgotten, in case a share only dropped for a moment: the row keeps its place and a later look is booked.
        Assert.Equal("on_hold", await ScalarText("SELECT status FROM files"));
        Assert.Contains("look again", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}' AND not_before IS NOT NULL"));
    }

    [Fact]
    public async Task A_file_still_gone_at_the_later_look_is_forgotten_and_the_manager_is_told_it_is_no_longer_there()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "gone.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "hgone", library, "gone.mkv"); return 0; });
        var origin = "\"origin\":{\"source_key\":\"deluno\",\"handoff_id\":\"hgone\",\"callback_path\":\"" + EventsPath + "\"}";
        var first = $$"""{"relative_media_path":"gone.mkv","library_id":{{library}},"trigger":"worker",{{origin}}}""";

        await Handler().HandleAsync(Context(7, first), CancellationToken.None);

        // Not final: the manager hears nothing on the first absence.
        Assert.Empty(_fixture.Http.Requests);

        var later = $$"""{"relative_media_path":"gone.mkv","library_id":{{library}},"trigger":"worker","gone_looks":1,{{origin}}}""";
        await Handler().HandleAsync(Context(8, later), CancellationToken.None);

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (WireObject)post.Json!;
        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("source_gone", WireConvert.Str(body["failureClass"]));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.False(body.ContainsKey("disposition"));
        Assert.Equal("The download is no longer there, so Weir had nothing to do.", WireConvert.Str(body["message"]));
    }

    [Fact]
    public async Task A_file_that_is_back_at_the_later_look_is_handed_back_normally()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "back.mkv");
        _folders.Source("back.mkv");
        var payload = $$"""{"relative_media_path":"back.mkv","library_id":{{library}},"trigger":"worker","gone_looks":1}""";

        await Handler().HandleAsync(Context(9, payload), CancellationToken.None);

        Assert.True(File.Exists(_folders.Out("back.mkv")));
        Assert.Equal("passed_through", await ScalarText("SELECT status FROM files"));
    }

    [Fact]
    public async Task A_file_that_comes_back_while_the_copy_settles_is_handed_back()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "blip.mkv");
        var payload = $$"""{"relative_media_path":"blip.mkv","library_id":{{library}},"trigger":"worker"}""";
        var clock = new TimerWatchingClock(_fixture.Store.Clock);
        var handler = new ProcessingPassThroughHandler(
            _fixture.Store.Database, clock, NullLogger<ProcessingPassThroughHandler>.Instance, _fixture.Handback, _fixture.Libraries, _fixture.Jobs, _fixture.Reporter)
        {
            GoneSettle = TimeSpan.FromSeconds(1.5),
        };

        var handling = handler.HandleAsync(Context(10, payload), CancellationToken.None);
        await Eventually.ThatAsync(() => clock.TimersSet >= 1);
        _folders.Source("blip.mkv");
        _fixture.Store.Clock.Advance(handler.GoneSettle);
        await handling;

        Assert.True(File.Exists(_folders.Out("blip.mkv")), "a file that was only briefly missing is still handed back");
        Assert.Equal("passed_through", await ScalarText("SELECT status FROM files"));
    }

    [Fact]
    public async Task A_file_whose_folder_was_deleted_settles_the_same_way()
    {
        var library = await LibraryAsync();
        _folders.Source(Path.Join("Release", "film.mkv"));
        Directory.Delete(Path.Join(_folders.Watched, "Release"), recursive: true);
        await FileRowAsync(library, "Release/film.mkv");
        var payload = $$"""{"relative_media_path":"Release/film.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Handler().HandleAsync(Context(5, payload), CancellationToken.None);

        Assert.Equal("on_hold", await ScalarText("SELECT status FROM files"));
        Assert.Equal("skipped", await ScalarText("SELECT result FROM activity_events"));
    }

    [Fact]
    public async Task A_missing_watched_folder_is_still_a_failure_not_a_file_that_is_gone()
    {
        var library = await LibraryAsync();
        await FileRowAsync(library, "file.mkv");
        Directory.Delete(_folders.Watched, recursive: true);
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Assert.ThrowsAsync<AlreadyRecordedFailureException>(() => Handler().HandleAsync(Context(6, payload), CancellationToken.None));

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal("failed", await ScalarText("SELECT result FROM activity_events"));
    }

    [Fact]
    public async Task Output_folder_same_as_watched_folder_is_refused_without_deleting_anything()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        var library = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Movies', 'movie', $w, $w, $k, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
        _folders.Source("file.mkv");
        await FileRowAsync(library, "file.mkv");
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Assert.ThrowsAsync<AlreadyRecordedFailureException>(() => Handler().HandleAsync(Context(3, payload), CancellationToken.None));
        Assert.True(File.Exists(_folders.Source("file.mkv")));
    }

    [Fact]
    public async Task An_existing_output_under_skip_is_respected_and_delivered_false_means_no_report()
    {
        var library = await LibraryAsync(collision: "skip");
        _folders.Source("file.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(_folders.Out("file.mkv"))!);
        File.WriteAllText(_folders.Out("file.mkv"), "already there");
        await FileRowAsync(library, "file.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "hskip", library, "file.mkv"); return 0; });
        var payload = $$$"""{"relative_media_path":"file.mkv","library_id":{{{library}}},"trigger":"worker","origin":{"source_key":"deluno","handoff_id":"hskip","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(4, payload), CancellationToken.None);

        Assert.Equal("already there", File.ReadAllText(_folders.Out("file.mkv")));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_passed_through'"));
        Assert.False(((WireBool)detail["delivered"]).Value);
        Assert.Equal("skip", WireConvert.Str(detail["collision_action"]));
    }

    [Fact]
    public async Task A_delivered_hand_off_is_reported_to_the_waiting_manager_with_its_output_path()
    {
        var library = await LibraryAsync();
        _folders.Source(Path.Join("Film", "film.mkv"));
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"trigger":"worker","origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}"}}""";

        await Handler().HandleAsync(Context(5, payload), CancellationToken.None);

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (WireObject)post.Json!;
        Assert.Equal(("h1", "completed"), (WireConvert.Str(body["handoffId"]), WireConvert.Str(body["status"])));
        Assert.Equal(Path.GetFullPath(_folders.Out(Path.Join("Film", "film.mkv"))), WireConvert.Str(body["outputPath"]));
        Assert.Equal(
            "Weir could not process this file, so it handed the original back unchanged; it is ready to import.",
            WireConvert.Str(body["message"]));
        Assert.Equal("passed-through", await ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }

    [Fact]
    public async Task A_delivery_that_did_not_come_from_a_manager_reports_nothing()
    {
        var library = await LibraryAsync();
        _folders.Source("solo.mkv");
        await FileRowAsync(library, "solo.mkv");
        var payload = $$"""{"relative_media_path":"solo.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Handler().HandleAsync(Context(6, payload), CancellationToken.None);

        Assert.Empty(_fixture.Http.Requests);
        Assert.Equal("passed_through", await ScalarText("SELECT status FROM files"));
    }

    /// <summary>More than any drive has, so the free-space check always finds the drive short.</summary>
    private const long MoreFreeThanAnyDriveHasMb = 1_000_000_000;

    [Fact]
    public async Task A_hand_back_waits_for_room_when_the_workflow_keeps_more_free_than_the_drive_has()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute($"UPDATE libraries SET minimum_free_disk_space_mb = {MoreFreeThanAnyDriveHasMb}");
        var source = _folders.Source("file.mkv", bytes: 5000);
        await FileRowAsync(library, "file.mkv");
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}},"trigger":"worker"}""";

        await Handler().HandleAsync(Context(7, payload), CancellationToken.None);

        Assert.False(File.Exists(_folders.Out("file.mkv")));
        Assert.True(File.Exists(source));
        Assert.Equal("on_hold", await ScalarText("SELECT status FROM files"));
        Assert.StartsWith("Waiting: the output drive has less than", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, await ScalarText("SELECT hold_until FROM files"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_passed_through'"));
        var booked = WireJsonParser.Parse(await ScalarText($"SELECT payload_json FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}' AND not_before IS NOT NULL"));
        Assert.Equal(1, (long)((WireInteger)((WireObject)booked)["disk_space_looks"]).Value);
    }

    [Fact]
    public async Task A_hand_back_that_has_already_waited_for_room_counts_one_more_look()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute($"UPDATE libraries SET minimum_free_disk_space_mb = {MoreFreeThanAnyDriveHasMb}");
        _folders.Source("file.mkv", bytes: 5000);
        await FileRowAsync(library, "file.mkv");
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}},"trigger":"worker","disk_space_looks":2}""";

        await Handler().HandleAsync(Context(8, payload), CancellationToken.None);

        var booked = WireJsonParser.Parse(await ScalarText($"SELECT payload_json FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}' AND not_before IS NOT NULL"));
        Assert.Equal(3, (long)((WireInteger)((WireObject)booked)["disk_space_looks"]).Value);
    }

    [Fact]
    public async Task A_hand_back_that_waited_for_room_is_delivered_once_there_is_room()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute($"UPDATE libraries SET minimum_free_disk_space_mb = {MoreFreeThanAnyDriveHasMb}");
        var source = _folders.Source("file.mkv", bytes: 5000);
        await FileRowAsync(library, "file.mkv");
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}},"trigger":"worker"}""";
        await Handler().HandleAsync(Context(8, payload), CancellationToken.None);
        var booked = await ScalarText($"SELECT payload_json FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}' AND not_before IS NOT NULL");
        await _fixture.Store.Execute("UPDATE libraries SET minimum_free_disk_space_mb = 0");

        await Handler().HandleAsync(Context(9, booked), CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(_folders.Out("file.mkv")));
        Assert.Equal("passed_through", await ScalarText("SELECT status FROM files"));
    }

    [Fact]
    public async Task Repeated_pass_through_jobs_for_the_same_file_share_one_dedupe_key()
    {
        var library = await LibraryAsync();
        var jobs = _fixture.Jobs;
        var body = $$"""{"relative_media_path":"x.mkv","library_id":{{library}},"trigger":"worker"}""";
        var first = await jobs.EnqueueOrGetAsync($"processing.file.pass_through.v1:{library}:x.mkv", IntakeRules.PassThroughJobKind, body);
        var second = await jobs.EnqueueOrGetAsync($"processing.file.pass_through.v1:{library}:x.mkv", IntakeRules.PassThroughJobKind, body);
        Assert.Equal(first.Id, second.Id);
    }
}
