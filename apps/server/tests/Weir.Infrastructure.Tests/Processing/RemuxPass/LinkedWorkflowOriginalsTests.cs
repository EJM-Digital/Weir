using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A workflow linked to a media manager never has its original download removed, whatever <c>remove_original_after_success</c>
/// says: the download client may still be seeding it. A workflow linked to Deluno is processed only from Deluno's hand-off,
/// never from Weir's own scan. A Weir-only workflow keeps removing originals. Real database, folders, scan, handler and worker
/// loop; only ffprobe/ffmpeg and the manager's HTTP are fakes.
/// </summary>
public sealed class LinkedWorkflowOriginalsTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string Release = "Film.2024";
    private const string Episode = "Show.S01";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private long _libraryId;

    public LinkedWorkflowOriginalsTests()
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        _fixture.Store.Execute("UPDATE operator_settings SET minimum_free_disk_space_mb = 0").GetAwaiter().GetResult();
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", """{"records":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        _fixture.DelunoListsNoLibraries();
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    /// <summary>A workflow that asks for its originals to be removed, linked to a manager of <paramref name="linkedKind"/> or to none.</summary>
    private async Task SetUpAsync(string mediaType, string? linkedKind, string rejectedFileAction = "leave", int minFileSizeMb = 0)
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, " +
            "sidecar_patterns_csv, remove_original_after_success) " +
            "VALUES ('Library', $t, $w, $o, $k, 'pass_through', 3, $action, 60, 0, $min, 1, '.nfo', 1) RETURNING id",
            ("$t", mediaType),
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$action", rejectedFileAction),
            ("$min", minFileSizeMb))), CultureInfo.InvariantCulture);
        if (linkedKind is not null)
        {
            var connection = await _fixture.AddConnectionAsync(linkedKind, "http://192.0.2.30:5099", "k1");
            await _fixture.Store.Execute($"INSERT INTO library_manager_links (library_id, connection_id) VALUES ({_libraryId}, {connection})");
        }

        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
        _media.Probes["Film.2024.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.Probes["Show.S01E01.mkv"] = FakeMediaRunner.EnglishAndJapanese;
    }

    /// <summary>The release folder a download client left: the video, an .nfo and a .txt.</summary>
    private (string Video, string Nfo, string Txt, string Folder) Download(string folder, string video)
    {
        var path = _folders.Source(Path.Join(folder, video));
        var nfo = Path.Join(_folders.Watched, folder, Path.ChangeExtension(video, ".nfo"));
        var txt = Path.Join(_folders.Watched, folder, "readme.txt");
        File.WriteAllText(nfo, "metadata");
        File.WriteAllText(txt, "notes");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        return (path, nfo, txt, Path.Join(_folders.Watched, folder));
    }

    private RemuxPassHandler RemuxHandler()
    {
        var data = new SqliteRemuxPassData(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);
        var runner = new RemuxPassRunner(
            new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System),
            new FixedResolver(),
            data,
            data,
            new TvSeasonFolderCleanup(_fixture.Store.Database, _fixture.Connections, TimeProvider.System, NullLogger<TvSeasonFolderCleanup>.Instance),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            TimeProvider.System,
            NullLogger<RemuxPassRunner>.Instance);
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
            new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance),
            _fixture.Reporter);
    }

    private async Task DrainAsync()
    {
        var worker = new ProcessingJobProcessor(
            _fixture.Jobs,
            new JobHandlerRegistry([RemuxHandler()]),
            new SqliteActivityWriter(_fixture.Store.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _fixture.Store.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);
        for (var i = 0; i < 20; i++)
        {
            if (await worker.ProcessOneAsync("test-worker") == JobProcessOutcome.Idle)
            {
                return;
            }
        }

        Assert.Fail("The worker never went idle.");
    }

    private async Task ScanAndDrainAsync(string mediaType)
    {
        var scan = new ProcessingWatchedFolderScanDispatchJobHandler(
            _fixture.Store.Database, _fixture.Store.Clock, _fixture.Store.Options, _fixture.Jobs, _fixture.Connections,
            new SuiteSettingsStore(new AuthStore()), _fixture.Libraries, _fixture.Files, new FileSkipMarkerStore());
        var payload = new WireObject().Set("enqueue_remux_jobs", true).Set("scan_trigger", "manual").Set("media_scope", mediaType).Set("library_id", _libraryId);
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            $"scan-{Guid.NewGuid():N}", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await scan.HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "scan-owner"), CancellationToken.None);
        await _fixture.Store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {job.Id}");
        await DrainAsync();
    }

    private Task<long> RemuxJobsAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");

    private async Task<string> ActivityDetailAsync()
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(detail, '\n') FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void AssertNothingRemoved((string Video, string Nfo, string Txt, string Folder) download)
    {
        Assert.True(File.Exists(download.Video), "the original download must stay where the download client put it");
        Assert.True(File.Exists(download.Nfo), "its .nfo must stay");
        Assert.True(File.Exists(download.Txt), "its .txt must stay");
        Assert.True(Directory.Exists(download.Folder), "its release folder must stay");
    }

    [Theory]
    [InlineData("radarr", "Radarr")]
    [InlineData("sonarr", "Sonarr")]
    public async Task A_movie_linked_to_a_manager_keeps_its_release_folder_even_when_the_workflow_asks_for_removal(string kind, string product)
    {
        await SetUpAsync("movie", kind);
        var download = Download(Release, "Film.2024.mkv");

        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(_folders.Out(Path.Join(Release, "Film.2024.mkv"))));
        AssertNothingRemoved(download);
        Assert.Contains($"This workflow is linked to {product}, so the original stays with your download client", await ActivityDetailAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tv_episode_linked_to_a_manager_keeps_its_season_folder_even_when_the_workflow_asks_for_removal()
    {
        await SetUpAsync("tv", "sonarr");
        var download = Download(Episode, "Show.S01E01.mkv");

        await ScanAndDrainAsync("tv");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(_folders.Out(Path.Join(Episode, "Show.S01E01.mkv"))));
        AssertNothingRemoved(download);
        Assert.Contains("This workflow is linked to Sonarr", await ActivityDetailAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_weir_only_workflow_that_asks_for_removal_still_removes_the_release_folder_with_its_sidecars()
    {
        await SetUpAsync("movie", linkedKind: null);
        var download = Download(Release, "Film.2024.mkv");

        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(_folders.Out(Path.Join(Release, "Film.2024.mkv"))));
        Assert.False(Directory.Exists(download.Folder));
        Assert.True(File.Exists(_folders.Out(Path.Join(Release, "Film.2024.nfo"))), "the sidecar was copied to the output before the folder went");
    }

    [Fact]
    public async Task A_deluno_workflow_is_not_processed_by_its_own_scan_but_is_by_the_hand_off_and_its_originals_stay()
    {
        await SetUpAsync("movie", "deluno");
        var download = Download(Release, "Film.2024.mkv");

        await ScanAndDrainAsync("movie");

        Assert.Equal(0, await RemuxJobsAsync());
        Assert.Empty(Directory.GetFileSystemEntries(_folders.Output));
        AssertNothingRemoved(download);

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = "handoff",
            MediaScope = "movie",
            FilePath = download.Video,
            HandoffId = "h1",
            CallbackPath = EventsPath,
            ReleaseName = "Film.2024",
            LibraryId = "lib-1",
        }));
        await DrainAsync();

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(_folders.Out(Path.Join(Release, "Film.2024.mkv"))));
        AssertNothingRemoved(download);
        Assert.Contains("This workflow is linked to Deluno, so the original stays with your download client", await ActivityDetailAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("radarr", false)]
    [InlineData("deluno", false)]
    [InlineData(null, true)]
    public async Task A_rejected_file_is_deleted_by_the_scan_only_for_a_weir_only_workflow(string? kind, bool deleted)
    {
        // The video is far smaller than the workflow's minimum size, so the scan rejects it and the workflow says to delete rejected files.
        await SetUpAsync("movie", kind, rejectedFileAction: "delete_file", minFileSizeMb: 50);
        var video = _folders.Source("tiny.mkv");

        await ScanAndDrainAsync("movie");

        Assert.Equal(0, await RemuxJobsAsync());
        Assert.Equal(!deleted, File.Exists(video));
    }

    [Fact]
    public async Task A_later_scan_never_finishes_removing_the_original_of_a_movie_a_linked_workflow_cleaned()
    {
        await SetUpAsync("movie", "radarr");
        var download = Download(Release, "Film.2024.mkv");
        await ScanAndDrainAsync("movie");
        Assert.Equal(1, await RemuxJobsAsync());

        // The output is complete and the original is still there, which for a Weir-only workflow is an interrupted removal the next
        // scan finishes. A linked workflow's original is not Weir's to remove, so the scans leave it.
        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        AssertNothingRemoved(download);
    }
    private async Task<string> StatusAsync()
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM files";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task SeedFileAsync(string status, string? nextRetryAt)
    {
        Download(Release, "Film.2024.mkv");
        await _fixture.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, failure_attempts, next_retry_at) " +
            $"VALUES ({_libraryId}, '{Release}/Film.2024.mkv', '{status}', 'ffmpeg failed.', 3, {(nextRetryAt is null ? "NULL" : "'" + nextRetryAt + "'")})");
    }

    private async Task QueueRetryAsync()
    {
        var payload = new WireObject()
            .Set("relative_media_path", $"{Release}/Film.2024.mkv")
            .Set("media_scope", "movie")
            .Set("library_id", _libraryId)
            .Set("trigger", "retry")
            .Set(HandoffRetries.PayloadMarker, true);
        await _fixture.Jobs.EnqueueOrGetAsync($"retry-{Guid.NewGuid():N}", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
    }

    [Fact]
    public async Task A_queued_retry_of_a_file_still_waiting_for_one_runs()
    {
        await SetUpAsync("movie", "deluno");
        await SeedFileAsync(ProcessingFileStatuses.ProcessingFailed, "2000-01-01 00:00:00");

        await QueueRetryAsync();
        await DrainAsync();

        Assert.True(File.Exists(_folders.Out(Path.Join(Release, "Film.2024.mkv"))));
        Assert.Single(_media.Remuxes);
    }

    [Theory]
    [InlineData(ProcessingFileStatuses.ProcessingFailed)]
    [InlineData(ProcessingFileStatuses.Processed)]
    [InlineData(ProcessingFileStatuses.Rejected)]
    [InlineData(ProcessingFileStatuses.PassedThrough)]
    public async Task A_queued_retry_drops_itself_when_its_file_was_given_up_on_or_finished_after_it_was_queued(string status)
    {
        await SetUpAsync("movie", "deluno");
        // No next attempt is booked: the attempt that failed last was the one the workflow allows.
        await SeedFileAsync(status, nextRetryAt: null);

        await QueueRetryAsync();
        await DrainAsync();

        Assert.Empty(_media.Remuxes);
        Assert.Empty(Directory.GetFileSystemEntries(_folders.Output));
        Assert.Equal(status, await StatusAsync());
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE status = 'completed'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
    }

    [Fact]
    public async Task The_attempt_that_reaches_the_limit_queues_no_retry_because_the_same_count_decides_both()
    {
        await SetUpAsync("movie", "deluno");
        await _fixture.Store.Execute($"UPDATE libraries SET max_attempts = 3 WHERE id = {_libraryId}");
        var origin = new WireObject().Set("source_key", "deluno").Set("handoff_id", "h1");
        var queued = new List<long>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await _fixture.Db(async uow =>
            {
                var library = (await _fixture.Libraries.GetAsync(uow, _libraryId))!;
                var decision = await RemuxPassFileState.RecordFailureAsync(
                    uow, library, $"{Release}/Film.2024.mkv", ProcessingFailureClasses.Execution, "ffmpeg failed.", DateTimeOffset.UtcNow);
                await HandoffRetries.QueueIfOwedAsync(
                    uow, new QueueingFailurePolicy(_fixture.Jobs), _fixture.Libraries, library, $"{Release}/Film.2024.mkv", origin, decision);
                return 0;
            });
            queued.Add(await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        }

        // One retry after each of the first two failures, and none after the third, which is the last the workflow allows.
        Assert.Equal([1L, 2L, 2L], queued);
    }
}
