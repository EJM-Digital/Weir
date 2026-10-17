using System.Globalization;
using Microsoft.Extensions.Logging;
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
/// A file Weir cannot read is never handed back, and a file, or the folder holding it, that is deleted while Weir has work queued
/// for it is nothing to do: one plain Information row, and no Error or Warning row, failed job or retry. Real database, folders,
/// scan, handlers and worker loop; only ffprobe and ffmpeg are fakes.
/// </summary>
public sealed class VanishedSourceTests : IDisposable
{
    private const string Rel = "Film.2024/Film.2024.mkv";

    private const string UnreadableProbe =
        "[mov,mp4,m4a,3gp,3g2,mj2 @ 000001b280590a00] moov atom not found\nC:\\Weir\\Ready\\Movies\\Film.2024.mkv: Invalid data found when processing input";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private long _libraryId;

    public VanishedSourceTests()
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        _fixture.Store.Execute("UPDATE operator_settings SET minimum_free_disk_space_mb = 0").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private async Task SetUpAsync(string failurePolicy = "pass_through")
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, sidecar_patterns_csv, remove_original_after_success) " +
            "VALUES ('Library', 'movie', $w, $o, $k, $policy, 3, 'leave', 60, 0, 0, 1, '.srt', 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$policy", failurePolicy))), CultureInfo.InvariantCulture);
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
        _media.Probes["Film.2024.mkv"] = FakeMediaRunner.EnglishAndJapanese;
    }

    private RemuxPassHandler RemuxHandler(Func<CancellationToken, Task>? goneLookAgain = null)
    {
        var data = new SqliteRemuxPassData(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);
        var runner = new RemuxPassRunner(
            new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), _fixture.Store.Clock),
            new FixedResolver(),
            data,
            data,
            new TvSeasonFolderCleanup(_fixture.Store.Database, _fixture.Connections, _fixture.Store.Clock, NullLogger<TvSeasonFolderCleanup>.Instance),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            _fixture.Store.Clock,
            NullLogger<RemuxPassRunner>.Instance);
        return new RemuxPassHandler(
            _fixture.Store.Database,
            _fixture.Store.Options,
            runner,
            new QueueingFailurePolicy(_fixture.Jobs),
            _fixture.OperatorSettings,
            _fixture.Handback,
            _fixture.Libraries,
            _fixture.Store.Clock,
            NullLogger<RemuxPassHandler>.Instance,
            new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance),
            _fixture.Reporter,
            _fixture.Jobs)
        {
            GoneSettle = TimeSpan.Zero,
            GoneLookAgain = goneLookAgain,
        };
    }

    private ProcessingPassThroughHandler PassThroughHandler() =>
        new(_fixture.Store.Database, _fixture.Store.Clock, NullLogger<ProcessingPassThroughHandler>.Instance, _fixture.Handback, _fixture.Libraries, _fixture.Jobs, _fixture.Reporter)
        {
            GoneSettle = TimeSpan.Zero,
        };

    private ProcessingRejectHandler RejectHandler() =>
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
            _fixture.Store.Clock,
            NullLogger<ProcessingRejectHandler>.Instance);

    private ProcessingJobProcessor Worker() => new(
        _fixture.Jobs,
        new JobHandlerRegistry([RemuxHandler(), PassThroughHandler(), RejectHandler()]),
        new SqliteActivityWriter(_fixture.Store.Database),
        new NoUnhandledJobFailureRecorder(),
        new NoJobNotifications(),
        _fixture.Store.Clock,
        NullLogger<ProcessingJobProcessor>.Instance);

    private ProcessingWatchedFolderScanDispatchJobHandler ScanHandler(ILogger<ProcessingWatchedFolderScanDispatchJobHandler>? logger = null) => new(
        _fixture.Store.Database, _fixture.Store.Clock, _fixture.Store.Options, _fixture.Jobs, _fixture.Connections,
        new SuiteSettingsStore(new AuthStore()), _fixture.Libraries, _fixture.Files, new FileSkipMarkerStore(), logger: logger)
    {
        GoneLookAgain = _ => Task.CompletedTask,
    };

    private async Task ScanAsync(ProcessingWatchedFolderScanDispatchJobHandler? handler = null)
    {
        var payload = new WireObject().Set("enqueue_remux_jobs", true).Set("scan_trigger", "watcher").Set("media_scope", "movie").Set("library_id", _libraryId);
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            $"scan-{Guid.NewGuid():N}", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        try
        {
            await (handler ?? ScanHandler()).HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "scan-owner"), CancellationToken.None);
        }
        finally
        {
            await _fixture.Store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {job.Id}");
        }
    }

    private async Task DrainAsync()
    {
        var worker = Worker();
        for (var i = 0; i < 20; i++)
        {
            if (await worker.ProcessOneAsync("test-worker") == JobProcessOutcome.Idle)
            {
                return;
            }
        }

        Assert.Fail("The worker never went idle.");
    }

    private async Task ScanAndDrainAsync()
    {
        await ScanAsync();
        await DrainAsync();
    }

    private void Advance(int minutes) => _fixture.Store.Clock.Set(_fixture.Store.Clock.GetUtcNow().AddMinutes(minutes));

    private Task<long> JobsOfKindAsync(string kind) => _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{kind}'");

    private Task<long> PassThroughJobsAsync() => JobsOfKindAsync(IntakeRules.PassThroughJobKind);

    /// <summary>Jobs that failed or are waiting to try again: neither is what a deleted file should cause.</summary>
    private Task<long> FailedOrRetriedJobsAsync() =>
        _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE status = 'failed' OR last_error IS NOT NULL OR attempt_count > 1");

    /// <summary>Activity a person would see as an error or a warning.</summary>
    private Task<long> ErrorsAndWarningsAsync() =>
        _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE result IN ('failed', 'warning', 'retrying')");

    private Task<long> FilesAsync() => _fixture.Store.Scalar("SELECT count(*) FROM files");

    private async Task<string?> StatusAsync(string relative)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM files WHERE relative_path = $p";
        command.Parameters.AddWithValue("$p", relative);
        return await command.ExecuteScalarAsync() is { } value and not DBNull ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private async Task<string> ReasonAsync(string relative)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_reason FROM files WHERE relative_path = $p";
        command.Parameters.AddWithValue("$p", relative);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>The rows that say the file is gone: the one Information row the operator is owed.</summary>
    private Task<long> GoneRowsAsync() =>
        _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE result = 'skipped' AND title LIKE '%is no longer there, so there is nothing to do'");

    /// <summary>Runs the scan and the waits until a file that cannot be read has used up all its looks.</summary>
    private async Task WaitOutTheLooksAsync(int looks)
    {
        for (var i = 0; i < looks; i++)
        {
            Advance(RemuxPassHandler.UnreadableWaitMinutes[i] + 1);
            await DrainAsync();
        }
    }

    // --- an unreadable file is never passed through -------------------------------------------------------------------

    [Fact]
    public async Task An_unreadable_file_that_is_still_there_when_its_wait_ends_is_left_where_it_is_under_a_reject_workflow()
    {
        await SetUpAsync(failurePolicy: "reject");
        _media.ProbeError = UnreadableProbe;
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync();
        Assert.Equal("on_hold", await StatusAsync(Rel));
        await WaitOutTheLooksAsync(RemuxPassHandler.UnreadableWaitMinutes.Count);
        await DrainAsync();

        // With no media manager to take the rejection, a reject workflow used to fall back to handing the file back.
        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.False(File.Exists(_folders.Out(Rel)), "a file Weir could not read is never handed back");
        Assert.True(File.Exists(source));
        Assert.Equal("rejected", await StatusAsync(Rel));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_reject_fell_back'"));
    }

    [Fact]
    public async Task An_unreadable_file_that_is_still_there_when_its_wait_ends_is_left_where_it_is_under_a_pass_through_workflow()
    {
        await SetUpAsync();
        _media.ProbeError = UnreadableProbe;
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync();
        await WaitOutTheLooksAsync(RemuxPassHandler.UnreadableWaitMinutes.Count);

        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.False(File.Exists(_folders.Out(Rel)));
        Assert.True(File.Exists(source));
        Assert.Equal("rejected", await StatusAsync(Rel));
    }

    // --- a file deleted while Weir waits is nothing to do -------------------------------------------------------------

    [Fact]
    public async Task An_unreadable_file_deleted_while_it_waits_settles_with_no_failed_job_and_one_information_row()
    {
        await SetUpAsync();
        _media.ProbeError = UnreadableProbe;
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync();
        Assert.Equal("on_hold", await StatusAsync(Rel));

        Directory.Delete(Path.Join(_folders.Watched, "Film.2024"), recursive: true);
        Advance(RemuxPassHandler.UnreadableWaitMinutes[0] + 1);
        await DrainAsync();

        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.Equal(0, await FailedOrRetriedJobsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(1, await GoneRowsAsync());
        Assert.False(File.Exists(_folders.Out(Rel)));

        // Held, not forgotten, while Weir cannot tell a deleted file from a share that dropped: the row keeps its place.
        Assert.Equal("on_hold", await StatusAsync(Rel));
        Assert.Contains("look again", await ReasonAsync(Rel), StringComparison.Ordinal);

        // Still gone once the grace has passed: the later look forgets it.
        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        Assert.Null(await StatusAsync(Rel));
        Assert.Equal(0, await FailedOrRetriedJobsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.Equal(1, await GoneRowsAsync());
    }

    [Fact]
    public async Task A_file_deleted_after_the_scan_queued_it_is_held_then_forgotten_with_no_failed_job_and_nothing_handed_back()
    {
        await SetUpAsync();
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAsync();
        Assert.Equal(1, await JobsOfKindAsync(RemuxPassOutcomes.JobKind));
        Directory.Delete(Path.Join(_folders.Watched, "Film.2024"), recursive: true);
        await DrainAsync();

        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.Equal(0, await FailedOrRetriedJobsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(1, await GoneRowsAsync());
        Assert.Equal("on_hold", await StatusAsync(Rel));
        Assert.Empty(_media.Remuxes);

        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        Assert.Equal(0, await FilesAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(1, await GoneRowsAsync());
    }

    [Fact]
    public async Task A_file_that_is_back_at_the_later_look_is_processed_normally_and_keeps_its_place()
    {
        await SetUpAsync();
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync();
        File.Move(source, source + ".away");
        await DrainAsync();
        Assert.Equal("on_hold", await StatusAsync(Rel));

        File.Move(source + ".away", source);
        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        Assert.Equal("processed", await StatusAsync(Rel));
        Assert.Single(_media.Remuxes);
        Assert.Equal(0, await ErrorsAndWarningsAsync());
    }

    [Fact]
    public async Task A_file_that_comes_back_while_the_pass_settles_is_processed_in_the_same_job()
    {
        await SetUpAsync();
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync();
        File.Move(source, source + ".away");
        var worker = new ProcessingJobProcessor(
            _fixture.Jobs,
            new JobHandlerRegistry([RemuxHandler(_ =>
            {
                File.Move(source + ".away", source);
                return Task.CompletedTask;
            })]),
            new SqliteActivityWriter(_fixture.Store.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _fixture.Store.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);

        await worker.ProcessOneAsync("test-worker");

        Assert.Equal("processed", await StatusAsync(Rel));
        Assert.Equal(0, await GoneRowsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
    }

    [Fact]
    public async Task A_cleaned_row_whose_file_is_gone_goes_back_to_processed_when_the_sweep_finds_it()
    {
        await SetUpAsync();
        await InsertCleanedRowAsync("processing_failed");

        await ScanAndDrainAsync();

        await AssertBackToHistoryAsync();
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder'"));
    }

    [Fact]
    public async Task A_cleaned_row_whose_file_is_gone_goes_back_to_processed_at_the_passs_final_look()
    {
        await SetUpAsync();
        await InsertCleanedRowAsync("processing_failed");
        var payload = new WireObject().Set("relative_media_path", Rel).Set("library_id", _libraryId).Set("media_scope", "movie").Set("trigger", "manual");
        await _fixture.Jobs.EnqueueOrGetAsync("process-again", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));

        // The first look holds it: it might only be a share that dropped.
        await DrainAsync();
        Assert.Equal("on_hold", await StatusAsync(Rel));

        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        await AssertBackToHistoryAsync();
        Assert.Equal(0, await ErrorsAndWarningsAsync());
    }

    /// <summary>A file a person pressed Process again on: its row is back in a working status but still carries the source Weir cleaned.</summary>
    private Task InsertCleanedRowAsync(string status) =>
        _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, failure_class, failure_attempts, next_retry_at, " +
            $"processed_source_size, processed_source_mtime_ns, last_seen_at, created_at, updated_at) " +
            $"VALUES ({_libraryId}, '{Rel}', '{status}', 'failed again', 2000, 'execution', 2, '{Ago(-30)}', 2000, 1700000000000000000, '{Ago(60)}', '{Ago(60)}', '{Ago(60)}')");

    /// <summary>Finished again, with the cleaned source kept and nothing left that makes the row wait on anyone.</summary>
    private async Task AssertBackToHistoryAsync()
    {
        Assert.Equal("processed", await StatusAsync(Rel));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM files WHERE processed_source_size = 2000 AND processed_source_mtime_ns = 1700000000000000000"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files WHERE failure_class IS NULL AND failure_attempts = 0 AND next_retry_at IS NULL AND hold_until IS NULL"));
        Assert.DoesNotContain("failed again", await ReasonAsync(Rel), StringComparison.Ordinal);

        var counts = await _fixture.Db(uow => _fixture.Files.StatusCountsAsync(uow, _libraryId));
        Assert.Equal(1, counts[ProcessingFileStatuses.Processed]);
        Assert.Equal(
            0,
            counts.Where(count => ProcessingFileMeanings.OfStatus.GetValueOrDefault(count.Key) is ProcessingFileMeaning.Attention or ProcessingFileMeaning.Broken or ProcessingFileMeaning.Todo or ProcessingFileMeaning.Doing)
                .Sum(count => count.Value));
    }

    private string Ago(int minutes) =>
        _fixture.Store.Clock.GetUtcNow().AddMinutes(-minutes).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_hand_off_is_told_nothing_on_the_first_absence_and_that_the_download_is_gone_once_it_is_forgotten()
    {
        await SetUpAsync();
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "hgone", _libraryId, Rel); return 0; });
        const string events = "/api/integrations/processors/events";
        var payload = new WireObject()
            .Set("relative_media_path", Rel)
            .Set("library_id", _libraryId)
            .Set("media_scope", "movie")
            .Set("trigger", "webhook")
            .Set("origin", new WireObject().Set("source_key", "deluno").Set("handoff_id", "hgone").Set("callback_path", events));
        await _fixture.Jobs.EnqueueOrGetAsync("hand-off-gone", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));

        await DrainAsync();

        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, events));
        Assert.Equal(0, await ErrorsAndWarningsAsync());

        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, events));
        var body = (WireObject)post.Json!;
        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("source_gone", WireConvert.Str(body["failureClass"]));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.False(body.ContainsKey("disposition"));
        Assert.Equal("The download is no longer there, so Weir had nothing to do.", WireConvert.Str(body["message"]));
    }

    [Fact]
    public async Task A_handed_back_file_deleted_before_its_copy_settles_with_no_failed_job()
    {
        await SetUpAsync();
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_libraryId}, '{Rel}', 'processing_failed', 'failed')");
        var payload = new WireObject().Set("relative_media_path", Rel).Set("library_id", _libraryId).Set("trigger", "worker");
        await _fixture.Jobs.EnqueueOrGetAsync("pass-through-1", IntakeRules.PassThroughJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        File.Delete(source);

        await DrainAsync();

        Assert.Equal(0, await FailedOrRetriedJobsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(1, await GoneRowsAsync());
        Assert.Equal("on_hold", await StatusAsync(Rel));

        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        Assert.Null(await StatusAsync(Rel));
        Assert.Equal(1, await GoneRowsAsync());
    }

    [Fact]
    public async Task A_handed_back_file_the_scan_already_found_gone_is_not_said_to_be_gone_a_second_time()
    {
        await SetUpAsync();
        await _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason, last_seen_at) " +
            $"VALUES ({_libraryId}, '{Rel}', 'on_hold', 'Waiting: the output drive has less than 5.0 GB free.', '{Ago(1)}')");
        var payload = new WireObject().Set("relative_media_path", Rel).Set("library_id", _libraryId).Set("trigger", "worker");
        await ScanAsync();
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync(Rel));
        Assert.Equal(1, await GoneRowsAsync());

        await _fixture.Jobs.EnqueueOrGetAsync("pass-through-1", IntakeRules.PassThroughJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await DrainAsync();

        Assert.Equal("on_hold", await StatusAsync(Rel));
        Assert.Equal(1, await GoneRowsAsync());
    }

    private Task InsertWaitingRowAsync() =>
        _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason, last_seen_at) " +
            $"VALUES ({_libraryId}, '{Rel}', 'on_hold', 'Weir is confirming that nothing is still writing to this file.', '{Ago(1)}')");

    [Fact]
    public async Task A_pass_that_finds_a_file_the_scan_already_held_as_gone_does_not_say_so_again()
    {
        await SetUpAsync();
        await InsertWaitingRowAsync();
        await ScanAsync();
        Assert.Equal(1, await GoneRowsAsync());
        var payload = new WireObject().Set("relative_media_path", Rel).Set("library_id", _libraryId).Set("media_scope", "movie").Set("trigger", "manual");

        await _fixture.Jobs.EnqueueOrGetAsync("process-gone", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await DrainAsync();

        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync(Rel));
        Assert.Equal(1, await GoneRowsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
    }

    [Fact]
    public async Task The_manager_is_told_a_download_is_gone_exactly_once_after_the_scan_held_it()
    {
        await SetUpAsync();
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "hscan", _libraryId, Rel); return 0; });
        const string events = "/api/integrations/processors/events";
        await InsertWaitingRowAsync();
        await ScanAsync();
        Assert.Equal(1, await GoneRowsAsync());
        var payload = new WireObject()
            .Set("relative_media_path", Rel)
            .Set("library_id", _libraryId)
            .Set("trigger", "worker")
            .Set("origin", new WireObject().Set("source_key", "deluno").Set("handoff_id", "hscan").Set("callback_path", events));

        await _fixture.Jobs.EnqueueOrGetAsync("pass-through-held", IntakeRules.PassThroughJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await DrainAsync();
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, events));

        Advance((int)GoneSources.LookAgainAfter.TotalMinutes + 1);
        await DrainAsync();

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, events));
        Assert.Equal("source_gone", WireConvert.Str(((WireObject)post.Json!)["failureClass"]));
        Assert.Equal(1, await GoneRowsAsync());
    }

    [Fact]
    public async Task A_pass_whose_file_is_back_at_the_last_look_and_then_vanishes_mid_write_leaves_no_progress_row_behind()
    {
        await SetUpAsync();
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        _media.RemuxError = "Error writing trailer";
        _media.OnCall = () =>
        {
            string[] last;
            lock (_media.Calls)
            {
                last = [.. _media.Calls[^1]];
            }

            if (last[0] == "ffmpeg" && last.Contains("-map") && !last.Contains("null"))
            {
                File.Delete(source);
            }
        };
        var payload = new WireObject()
            .Set("relative_media_path", Rel)
            .Set("library_id", _libraryId)
            .Set("media_scope", "movie")
            .Set("trigger", "manual")
            .Set("gone_looks", 1);

        await _fixture.Jobs.EnqueueOrGetAsync("process-last-look", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await DrainAsync();

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_processing_progress'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE result = 'running'"));
        Assert.Equal(1, await GoneRowsAsync());
        Assert.Equal(0, await FailedOrRetriedJobsAsync());
    }

    // --- scanning ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scan_after_a_watched_subfolder_was_deleted_has_nothing_to_queue_and_does_not_fail()
    {
        await SetUpAsync();
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        _folders.Source(Path.Join("Other.2023", "Other.2023.mkv"));
        _media.ProbeError = UnreadableProbe;
        await ScanAndDrainAsync();
        Directory.Delete(Path.Join(_folders.Watched, "Film.2024"), recursive: true);

        await ScanAndDrainAsync();

        Assert.Equal(0, await FailedOrRetriedJobsAsync());
        Assert.Equal(0, await ErrorsAndWarningsAsync());
        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.True(File.Exists(Path.Join(_folders.Watched, "Other.2023", "Other.2023.mkv")));
    }

    [Fact]
    public async Task A_scan_of_a_watched_folder_that_is_gone_warns_in_plain_words_and_does_not_fail_the_job()
    {
        await SetUpAsync();
        Directory.Delete(_folders.Watched, recursive: true);
        var log = new ListLogger<ProcessingWatchedFolderScanDispatchJobHandler>();
        var scan = ScanHandler(log);

        await ScanAsync(scan);

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("can't see its watched folder", warning.Message, StringComparison.Ordinal);
        Assert.Contains(_folders.Watched, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Equal(0, await JobsOfKindAsync(RemuxPassOutcomes.JobKind));
    }

    [Fact]
    public async Task The_missing_watched_folder_warning_is_said_once_until_the_folder_is_back_and_the_return_is_said_once()
    {
        await SetUpAsync();
        Directory.Delete(_folders.Watched, recursive: true);
        var log = new ListLogger<ProcessingWatchedFolderScanDispatchJobHandler>();
        var scan = ScanHandler(log);

        await ScanAsync(scan);
        await ScanAsync(scan);
        await ScanAsync(scan);

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Directory.CreateDirectory(_folders.Watched);
        await ScanAsync(scan);
        await ScanAsync(scan);

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("is back", StringComparison.Ordinal));
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Directory.Delete(_folders.Watched, recursive: true);
        await ScanAsync(scan);

        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    // --- a reject workflow and an unreadable file --------------------------------------------------------------------

    private async Task<JobWorkContext> UnreadableRejectJobAsync(int attempt, int attempts)
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "hunreadable", _libraryId, Rel); return 0; });
        var payload = new WireObject()
            .Set("relative_media_path", Rel)
            .Set("library_id", _libraryId)
            .Set("reason", "Weir couldn't read this file.")
            .Set("failure_class", "preflight")
            .Set("rejection_kind", RejectionKinds.UnreadableFile)
            .Set("origin", new WireObject().Set("source_key", "deluno").Set("handoff_id", "hunreadable").Set("callback_path", "/api/integrations/processors/events"));
        return new JobWorkContext(1, IntakeRules.RejectJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact), "test", attempt, attempts);
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_gets_the_rejection_of_an_unreadable_file_again_before_the_file_is_left_alone()
    {
        await SetUpAsync(failurePolicy: "reject");
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        // No route answers, so the manager is unreachable: the job is retried like any transient failure.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RejectHandler().HandleAsync(await UnreadableRejectJobAsync(1, 3), CancellationToken.None));
        Assert.Null(await StatusAsync(Rel));
        Assert.Equal(0, await PassThroughJobsAsync());

        // The last attempt fails too: the file stays where it is, rejected, and is never handed back.
        await RejectHandler().HandleAsync(await UnreadableRejectJobAsync(3, 3), CancellationToken.None);

        Assert.Equal("rejected", await StatusAsync(Rel));
        Assert.Equal(0, await PassThroughJobsAsync());
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(_folders.Out(Rel)));
    }

    [Fact]
    public async Task The_words_for_an_unreadable_file_left_in_place_say_only_what_happened()
    {
        await SetUpAsync(failurePolicy: "reject");
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        var payload = new WireObject()
            .Set("relative_media_path", Rel)
            .Set("library_id", _libraryId)
            .Set("reason", "Weir couldn't read this file.")
            .Set("rejection_kind", RejectionKinds.UnreadableFile);

        // No media manager is linked at all, so there is nobody to ask and nothing to retry.
        await RejectHandler().HandleAsync(
            new JobWorkContext(2, IntakeRules.RejectJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact), "test", 1, 3), CancellationToken.None);

        var reason = await ReasonAsync(Rel);
        Assert.Contains("left the file where it is", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("handed the original back", reason, StringComparison.Ordinal);
        Assert.Equal(0, await PassThroughJobsAsync());
    }
}
