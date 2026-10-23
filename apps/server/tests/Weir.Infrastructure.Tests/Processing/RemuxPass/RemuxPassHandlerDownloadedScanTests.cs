using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The optional downloaded-scan hand-back (#768): after a live pass writes a file to a library's output folder, an
/// enabled Sonarr/Radarr connection linked to that library and opted in gets its Downloaded Scan command, with the
/// path translated through the manager's own remote path mapping; a manager that cannot be reached never fails the
/// pass, and the setting is off unless a connection turns it on.
/// </summary>
public sealed class RemuxPassHandlerDownloadedScanTests : IDisposable
{
    private const string CommandPath = "/api/v3/command";
    private const string MappingPath = "/api/v3/remotepathmapping";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();

    public RemuxPassHandlerDownloadedScanTests() =>
        _fixture.Store.Execute("UPDATE operator_settings SET minimum_free_disk_space_mb = 0")
            .GetAwaiter().GetResult();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private RemuxPassHandler Handler()
    {
        var data = new SqliteRemuxPassData(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);
        var runner = new RemuxPassRunner(
            new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System),
            new FixedResolver(),
            data,
            data,
            new SkippedTvSeasonFolderCleanup(),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            TimeProvider.System,
            NullLogger<RemuxPassRunner>.Instance);
        var downloadedScan = new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance);
        return new RemuxPassHandler(
            _fixture.Store.Database,
            _fixture.Store.Options,
            runner,
            new QueueingFailurePolicy(_fixture.Jobs),
            _fixture.OperatorSettings,
            _fixture.Handback,
            _fixture.Libraries,
            TimeProvider.System,
            NullLogger<RemuxPassHandler>.Instance,
            downloadedScan,
            _fixture.Reporter,
            _fixture.Jobs);
    }

    private async Task<long> LibraryAsync()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, 'pass_through', 3, 'leave', 60, 0, 0, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
    }

    private async Task<long> ArrConnectionAsync(long libraryId, bool downloadedScanEnabled)
    {
        var connectionId = await _fixture.AddConnectionAsync("radarr", "http://192.0.2.60:8989", "key");
        if (downloadedScanEnabled)
        {
            await _fixture.Store.Execute($"UPDATE media_manager_connections SET downloaded_scan_enabled = 1 WHERE id = {connectionId}");
        }

        await _fixture.Db(async uow => { await _fixture.Libraries.SetManagerLinksAsync(uow, libraryId, [connectionId]); return 0; });
        return connectionId;
    }

    private async Task FileRowAsync(long libraryId, string relativePath) =>
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({libraryId}, '{relativePath}', 'unprocessed', '')");

    private static JobWorkContext Context(long id, string payload) => new(id, RemuxPassOutcomes.JobKind, payload, "test");

    private async Task RunPassAsync(long libraryId)
    {
        _folders.Source(Path.Join("Movie", "file.mkv"));
        _media.Probes["file.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await FileRowAsync(libraryId, "Movie/file.mkv");
        var payload = $$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{libraryId}}}""";

        await Handler().HandleAsync(Context(1, payload), CancellationToken.None);
    }

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task An_opted_in_connection_gets_the_scan_command_with_the_path_translated_through_its_mapping()
    {
        var library = await LibraryAsync();
        await ArrConnectionAsync(library, downloadedScanEnabled: true);
        _fixture.Http
            .Json(HttpMethod.Get, MappingPath, $$"""[{"host":"qbittorrent","remotePath":"/downloads/complete","localPath":{{WireJsonWriter.Dumps(new WireString(_folders.Output), WireJsonFormat.Compact)}},"id":1}]""")
            .Json(HttpMethod.Post, CommandPath, "{}");

        await RunPassAsync(library);

        var request = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, CommandPath));
        var body = (WireObject)request.Json!;
        Assert.Equal("DownloadedMoviesScan", WireConvert.Str(body["name"]));
        Assert.Equal("/downloads/complete/Movie/file.mkv", WireConvert.Str(body["path"]));
        Assert.Equal("Move", WireConvert.Str(body["importMode"]));
        Assert.False(body.ContainsKey("downloadClientId"));
        Assert.Equal("key", request.Headers["X-Api-Key"]);
        Assert.Equal("processed", await ScalarText("SELECT status FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.downloaded_scan_requested'"));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.downloaded_scan_requested'"));
        Assert.Equal("success", WireConvert.Str(detail["result"]));
    }

    [Fact]
    public async Task The_setting_is_off_by_default_and_no_command_is_sent_when_it_is_off()
    {
        var library = await LibraryAsync();
        var connectionId = await ArrConnectionAsync(library, downloadedScanEnabled: false);
        var row = await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, connectionId));

        await RunPassAsync(library);

        Assert.False(row!.DownloadedScanEnabled);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, CommandPath));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.downloaded_scan_requested'"));
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_never_fails_the_pass_and_the_failure_is_recorded_in_activity()
    {
        var library = await LibraryAsync();
        await ArrConnectionAsync(library, downloadedScanEnabled: true);
        _fixture.Http
            .Json(HttpMethod.Get, MappingPath, "[]")
            .Json(HttpMethod.Post, CommandPath, "", HttpStatusCode.InternalServerError);

        await RunPassAsync(library);

        // The pass itself succeeded on disk regardless of the manager's answer.
        Assert.Equal("processed", await ScalarText("SELECT status FROM files"));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.downloaded_scan_requested'"));
        Assert.Equal("failed", WireConvert.Str(detail["result"]));
        Assert.False(((WireBool)detail["accepted"]).Value);
        Assert.Contains("HTTP 500", WireConvert.Str(detail["reason"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_whose_note_cannot_be_written_never_fails_the_pass()
    {
        var library = await LibraryAsync();
        await ArrConnectionAsync(library, downloadedScanEnabled: true);
        _fixture.Http
            .Json(HttpMethod.Get, MappingPath, "[]")
            .Json(HttpMethod.Post, CommandPath, "{}");
        await _fixture.Store.Execute(
            "CREATE TRIGGER refuse_scan_note BEFORE INSERT ON activity_events WHEN NEW.event_type = 'processing.downloaded_scan_requested' BEGIN SELECT RAISE(ABORT, 'note refused'); END");

        await RunPassAsync(library);

        Assert.Equal("processed", await ScalarText("SELECT status FROM files"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.downloaded_scan_requested'"));
    }

    [Fact]
    public async Task No_manager_connection_at_all_sends_no_request_and_still_processes_the_file()
    {
        var library = await LibraryAsync();

        await RunPassAsync(library);

        Assert.Empty(_fixture.Http.Requests);
        Assert.Equal("processed", await ScalarText("SELECT status FROM files"));
    }
}
