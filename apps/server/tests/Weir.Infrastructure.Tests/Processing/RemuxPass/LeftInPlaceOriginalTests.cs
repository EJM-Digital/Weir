using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.IO;
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
/// What the watched-folder scan does with a file it has already dealt with, and with one that has gone. Real database,
/// folders, scan, handler and worker loop; only ffprobe and ffmpeg are fakes.
/// <list type="bullet">
/// <item>#644: a library that removes originals but had to leave one in place (TV season cleanup skipped, a Movies removal
/// interrupted) must not clean that file again on every scan once the manager has imported the output.</item>
/// <item>#645: a file that leaves the watched folder before Weir finishes with it stops being listed.</item>
/// <item>#643: a file whose queued pass was cancelled is left alone until it changes.</item>
/// </list>
/// </summary>
public sealed class LeftInPlaceOriginalTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private readonly ListLogger<RemuxPassRunner> _runnerLog = new();
    private long _libraryId;

    public LeftInPlaceOriginalTests()
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        _fixture.Store.Execute(
            "UPDATE operator_settings SET minimum_free_disk_space_mb = 0")
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private async Task SetUpAsync(string mediaType, bool removeOriginal = true, int readyAfterSeconds = 0)
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, " +
            "sidecar_patterns_csv, remove_original_after_success) " +
            "VALUES ('Library', $t, $w, $o, $k, 'pass_through', 3, 'leave', 60, $ready, 0, 1, '.srt', $remove) RETURNING id",
            ("$t", mediaType),
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$ready", readyAfterSeconds),
            ("$remove", removeOriginal ? 1 : 0))), CultureInfo.InvariantCulture);
        // Sources carry a Japanese track the rules drop; the written output is probed with the default and reads English only.
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
        _media.Probes["Film.2024.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.Probes["Show.S01E01.mkv"] = FakeMediaRunner.EnglishAndJapanese;
    }

    private RemuxPassHandler RemuxHandler()
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
            _runnerLog);
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
            _fixture.Jobs);
    }

    private ProcessingJobProcessor Worker() => new(
        _fixture.Jobs,
        new JobHandlerRegistry([RemuxHandler()]),
        new SqliteActivityWriter(_fixture.Store.Database),
        new NoUnhandledJobFailureRecorder(),
        new NoJobNotifications(),
        _fixture.Store.Clock,
        NullLogger<ProcessingJobProcessor>.Instance);

    /// <param name="mediaType">The scope to scan.</param>
    /// <param name="lookAgain">What the scan does between its first look at a file that is gone and the look that acts on it; nothing, when null.</param>
    private async Task ScanAsync(string mediaType, Func<CancellationToken, Task>? lookAgain = null)
    {
        var scan = new ProcessingWatchedFolderScanDispatchJobHandler(
            _fixture.Store.Database, _fixture.Store.Clock, _fixture.Store.Options, _fixture.Jobs, _fixture.Connections,
            new SuiteSettingsStore(new AuthStore()), _fixture.Libraries, _fixture.Files, new FileSkipMarkerStore())
        {
            GoneLookAgain = lookAgain ?? (_ => Task.CompletedTask),
        };
        var payload = new WireObject().Set("enqueue_remux_jobs", true).Set("scan_trigger", "watcher").Set("media_scope", mediaType).Set("library_id", _libraryId);
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            $"scan-{Guid.NewGuid():N}", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        try
        {
            await scan.HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "scan-owner"), CancellationToken.None);
        }
        finally
        {
            await _fixture.Store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {job.Id}");
        }
    }

    /// <summary>Move the clock on, so a look Weir queued for later is due.</summary>
    private void Advance(int minutes) => _fixture.Store.Clock.Set(_fixture.Store.Clock.GetUtcNow().AddMinutes(minutes));

    /// <summary>How many times ffmpeg read a file from start to finish.</summary>
    private int IntegrityReads() => _media.Calls.Count(argv => argv.Contains("-xerror"));

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

    private async Task ScanAndDrainAsync(string mediaType)
    {
        await ScanAsync(mediaType);
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

    private Task<long> RemuxJobsAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");

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

    private async Task<DateTimeOffset?> HoldUntilAsync(string relative)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hold_until FROM files WHERE relative_path = $p";
        command.Parameters.AddWithValue("$p", relative);
        return TimestampColumns.Parse(await command.ExecuteScalarAsync());
    }

    private Task<long> LeftActivityAsync() =>
        _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder'");

    /// <summary>
    /// A row as a scan leaves it. <paramref name="lastSeenMinutesAgo"/> null is a row no scan has seen, like a hand-off's file
    /// recorded on receipt; <paramref name="writtenMinutesAgo"/> is when the row was last written.
    /// </summary>
    private Task InsertRowAsync(string relative, string status, long size = 2000, int? lastSeenMinutesAgo = 60, int writtenMinutesAgo = 60)
    {
        string At(int minutes) =>
            "'" + _fixture.Store.Clock.GetUtcNow().AddMinutes(-minutes).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
        var lastSeen = lastSeenMinutesAgo is { } minutes ? At(minutes) : "NULL";
        return _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at, created_at, updated_at) " +
            $"VALUES ({_libraryId}, '{relative}', '{status}', 'seeded', {size}, {lastSeen}, {At(writtenMinutesAgo)}, {At(writtenMinutesAgo)})");
    }

    // --- #644 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tv_episode_whose_season_cleanup_was_skipped_is_not_cleaned_again()
    {
        await SetUpAsync("tv");
        var episode = _folders.Source(Path.Join("Show.S01", "Show.S01E01.mkv"));

        await ScanAndDrainAsync("tv");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(episode), "no TV manager could be asked, so Weir left the season folder in place");

        // TV passes record no source size, so before #644 even this rescan, with the output still there, cleaned it again.
        await ScanAndDrainAsync("tv");
        Assert.Equal(1, await RemuxJobsAsync());

        // The manager imports it and the output goes: still finished.
        File.Delete(_folders.Out(Path.Join("Show.S01", "Show.S01E01.mkv")));
        await ScanAndDrainAsync("tv");
        await ScanAndDrainAsync("tv");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.Single(_media.Remuxes);
        Assert.Equal("processed", await StatusAsync("Show.S01/Show.S01E01.mkv"));
    }

    [Fact]
    public async Task A_movie_directly_in_the_watched_root_is_left_alone_without_a_warning()
    {
        await SetUpAsync("movie");
        var source = _folders.Source("Film.2024.mkv");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        await ScanAndDrainAsync("movie");

        Assert.Equal("processed", await StatusAsync("Film.2024.mkv"));
        Assert.True(File.Exists(source), "there is no release folder to remove, and the file is not Weir's to delete");
        Assert.DoesNotContain(_runnerLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(_runnerLog.Entries, entry => entry.Message.Contains("no release folder to remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_movie_left_in_the_watched_root_is_not_cleaned_again_once_its_output_is_imported()
    {
        await SetUpAsync("movie");
        var source = _folders.Source("Film.2024.mkv");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        await ScanAndDrainAsync("movie");
        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(source), "a file directly in the watched root has no release folder to remove");

        File.Delete(_folders.Out("Film.2024.mkv"));
        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.Single(_media.Remuxes);
        Assert.Equal("processed", await StatusAsync("Film.2024.mkv"));
    }

    [Fact]
    public async Task A_replaced_episode_is_still_cleaned_in_a_library_that_removes_originals()
    {
        await SetUpAsync("tv");
        var episode = _folders.Source(Path.Join("Show.S01", "Show.S01E01.mkv"));
        await ScanAndDrainAsync("tv");
        Assert.Equal(1, await RemuxJobsAsync());

        // A better release lands at the same path: a different size and a later time.
        File.WriteAllBytes(episode, Enumerable.Repeat((byte)'y', 3000).ToArray());
        File.SetLastWriteTimeUtc(episode, DateTime.UtcNow.AddMinutes(5));
        await ScanAndDrainAsync("tv");

        Assert.Equal(2, await RemuxJobsAsync());
        Assert.Equal("processed", await StatusAsync("Show.S01/Show.S01E01.mkv"));
    }

    [WindowsFact("Needs Windows' mandatory file locks.")]
    public async Task An_interrupted_movie_removal_is_finished_while_the_output_is_there_and_never_cleans_it_again()
    {
        await SetUpAsync("movie");
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        // Readable, so the pass can clean it, but not deletable: the release folder cannot be removed.
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ScanAndDrainAsync("movie");
            Assert.Equal(1, await RemuxJobsAsync());
            Assert.True(File.Exists(source), "the lock stopped Weir removing the release folder");

            // Still locked: the removal waits, the file stays processed, and nothing is cleaned again.
            await ScanAndDrainAsync("movie");
            Assert.Equal(1, await RemuxJobsAsync());
            Assert.Equal("processed", await StatusAsync("Film.2024/Film.2024.mkv"));
        }

        // The lock clears and the output is still there: the next scan finishes the removal.
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.False(Directory.Exists(Path.Join(_folders.Watched, "Film.2024")));
        Assert.Equal("processed", await StatusAsync("Film.2024/Film.2024.mkv"));
    }

    [WindowsFact("Needs Windows' mandatory file locks.")]
    public async Task Every_retried_movie_removal_records_its_result_on_the_file()
    {
        await SetUpAsync("movie");
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ScanAndDrainAsync("movie");
            await ScanAsync("movie");
            Assert.Contains("removing the original download is waiting", await ReasonAsync("Film.2024/Film.2024.mkv"), StringComparison.Ordinal);
        }

        await ScanAsync("movie");

        Assert.False(Directory.Exists(Path.Join(_folders.Watched, "Film.2024")));
        Assert.Equal("processed", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Contains("finished removing the source release folder", await ReasonAsync("Film.2024/Film.2024.mkv"), StringComparison.Ordinal);
    }

    [WindowsFact("Needs Windows' mandatory file locks.")]
    public async Task An_interrupted_movie_removal_is_not_cleaned_again_after_the_output_is_imported()
    {
        await SetUpAsync("movie");
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        // Readable, so the pass can clean it, but not deletable: the release folder cannot be removed.
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ScanAndDrainAsync("movie");
        }

        File.Delete(_folders.Out(Path.Join("Film.2024", "Film.2024.mkv")));
        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
        Assert.True(File.Exists(source), "with no validated output left, Weir does not remove the original");
        Assert.Equal("processed", await StatusAsync("Film.2024/Film.2024.mkv"));
    }

    // --- #645 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_held_file_that_leaves_the_watched_folder_says_so_at_once_and_is_forgotten_later_with_no_second_entry()
    {
        await SetUpAsync("movie", readyAfterSeconds: 3600);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAsync("movie");
        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.NotEqual(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));

        File.Delete(source);
        await ScanAsync("movie");
        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, await LeftActivityAsync());

        // Looked at again before the grace is over: still listed, still one entry.
        await ScanAsync("movie");
        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, await LeftActivityAsync());

        // Gone for longer than a download client takes to move a file.
        _fixture.Store.Clock.Set(_fixture.Store.Clock.GetUtcNow().AddMinutes(11));
        await ScanAsync("movie");

        Assert.Null(await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, await LeftActivityAsync());
        Assert.Equal(0, await RemuxJobsAsync());

        await ScanAsync("movie");
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task Waiting_failed_and_cancelled_rows_for_files_that_are_gone_are_forgotten_but_outcomes_stay()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/waiting.mkv", ProcessingFileStatuses.Unprocessed);
        await InsertRowAsync("Gone/failed.mkv", ProcessingFileStatuses.ProcessingFailed);
        await InsertRowAsync("Gone/outside.mkv", ProcessingFileStatuses.OutOfSchedule);
        await InsertRowAsync("Gone/processed.mkv", ProcessingFileStatuses.Processed);
        await InsertRowAsync("Gone/passed.mkv", ProcessingFileStatuses.PassedThrough);
        await InsertRowAsync("Gone/cancelled.mkv", ProcessingFileStatuses.Cancelled);

        await ScanAsync("movie");

        Assert.Null(await StatusAsync("Gone/waiting.mkv"));
        Assert.Null(await StatusAsync("Gone/failed.mkv"));
        Assert.Null(await StatusAsync("Gone/outside.mkv"));
        Assert.Equal("processed", await StatusAsync("Gone/processed.mkv"));
        Assert.Equal("passed_through", await StatusAsync("Gone/passed.mkv"));
        // A cancelled hand-off is one Weir never started; with its file gone there is nothing left to queue again.
        Assert.Null(await StatusAsync("Gone/cancelled.mkv"));
        Assert.Equal(4, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_row_seen_moments_ago_is_held_as_gone_and_a_hand_off_row_written_moments_ago_is_kept()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/just-seen.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: 2);
        await InsertRowAsync("Gone/just-failed.mkv", ProcessingFileStatuses.ProcessingFailed, lastSeenMinutesAgo: 2);
        await InsertRowAsync("Gone/just-handed-off.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: null, writtenMinutesAgo: 2);

        await ScanAsync("movie");

        Assert.Equal("on_hold", await StatusAsync("Gone/just-seen.mkv"));
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Gone/just-seen.mkv"));
        Assert.NotNull(await HoldUntilAsync("Gone/just-seen.mkv"));
        Assert.Equal("processing_failed", await StatusAsync("Gone/just-failed.mkv"));
        Assert.Equal("unprocessed", await StatusAsync("Gone/just-handed-off.mkv"));
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_file_waiting_outside_its_hours_or_for_a_manager_that_leaves_the_watched_folder_is_held_as_gone()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/outside.mkv", ProcessingFileStatuses.OutOfSchedule, lastSeenMinutesAgo: 1);
        await InsertRowAsync("Gone/importing.mkv", ProcessingFileStatuses.BlockedUpstream, lastSeenMinutesAgo: 1);
        await _fixture.Store.Execute("UPDATE files SET status_reason = 'The Library workflow only runs inside its scheduled hours, and now is outside them.' WHERE relative_path = 'Gone/outside.mkv'");
        await _fixture.Store.Execute("UPDATE files SET blocked_by_connection = 'Deluno' WHERE relative_path = 'Gone/importing.mkv'");

        await ScanAsync("movie");
        await ScanAsync("movie");

        foreach (var path in new[] { "Gone/outside.mkv", "Gone/importing.mkv" })
        {
            Assert.Equal("on_hold", await StatusAsync(path));
            Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync(path));
        }

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files WHERE blocked_by_connection IS NOT NULL"));
        Assert.Equal(2, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_file_that_is_back_when_the_scan_looks_again_is_not_held_as_gone()
    {
        await SetUpAsync("movie", readyAfterSeconds: 3600);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync("movie");
        File.Move(source, source + ".away");

        // The file comes back between the scan's first look and its second.
        await ScanAsync("movie", _ =>
        {
            File.Move(source + ".away", source);
            return Task.CompletedTask;
        });

        Assert.NotEqual(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task The_scan_waits_the_set_time_before_it_believes_a_file_is_gone()
    {
        await SetUpAsync("movie", readyAfterSeconds: 3600);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync("movie");
        File.Delete(source);
        var waited = false;

        await ScanAsync("movie", async _ =>
        {
            Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
            Assert.NotEqual(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
            waited = true;
        });

        Assert.True(waited, "the scan waited before it believed the file was gone");
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, await LeftActivityAsync());
    }

    private VanishedFileSweepTask Sweep(Func<CancellationToken, Task>? lookAgain = null) =>
        new(_fixture.Store.Database, _fixture.Store.Options, _fixture.Libraries, _fixture.Store.Clock, NullLogger<VanishedFileSweepTask>.Instance)
        {
            GoneLookAgain = lookAgain ?? (_ => Task.CompletedTask),
        };

    private Task InsertHeldAsGoneRowAsync(string relative, long size = 2000, string? cleanedSource = null, int lastSeenMinutesAgo = 1)
    {
        string At(int minutes) =>
            "'" + _fixture.Store.Clock.GetUtcNow().AddMinutes(-minutes).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
        return _fixture.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, hold_until, last_seen_at, created_at, updated_at" +
            (cleanedSource is null ? string.Empty : ", processed_source_size, processed_source_mtime_ns") + ") " +
            $"VALUES ({_libraryId}, '{relative}', 'on_hold', '{GoneSourceText.HeldReason}', {size}, {At(-5)}, {At(lastSeenMinutesAgo)}, {At(60)}, {At(60)}" +
            (cleanedSource is null ? string.Empty : $", {cleanedSource}") + ")");
    }

    [Fact]
    public async Task Two_looks_at_the_same_file_at_once_hold_it_and_say_so_once()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/importing.mkv", ProcessingFileStatuses.OnHold, lastSeenMinutesAgo: 1);
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task BothHaveLooked(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }

            return both.Task;
        }

        Task<VanishedFiles.Changes> Look(string trigger) => VanishedFiles.SettleAsync(
            _fixture.Store.Database, _libraryId, _folders.Watched, "movie", _fixture.Store.Clock.GetUtcNow(), trigger, BothHaveLooked, _ => { }, PhysicalFileSystem.Instance, CancellationToken.None);

        var looks = await Task.WhenAll(Look("folder_change"), Look("scheduled"));

        Assert.Equal(1, looks.Sum(changes => changes.Held.Count));
        Assert.Equal(1, await LeftActivityAsync());
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Gone/importing.mkv"));
    }

    [Fact]
    public async Task The_entry_for_a_gone_file_names_the_module_and_action_and_why()
    {
        await SetUpAsync("movie", readyAfterSeconds: 3600);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync("movie");
        File.Delete(source);

        await ScanAsync("movie");

        Assert.Equal(
            1,
            await _fixture.Store.Scalar(
                "SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder' AND module = 'processing' " +
                "AND json_extract(detail, '$.module') = 'processing' AND json_extract(detail, '$.action') = 'scan' " +
                "AND json_extract(detail, '$.trigger') = 'manual' AND json_extract(detail, '$.result') = 'skipped'"));
    }

    [Fact]
    public async Task A_held_file_that_is_back_when_the_scan_next_looks_is_treated_as_any_new_file()
    {
        await SetUpAsync("movie", readyAfterSeconds: 3600);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await ScanAsync("movie");
        File.Move(source, source + ".away");
        await ScanAsync("movie");
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));

        File.Move(source + ".away", source);
        await ScanAsync("movie");

        Assert.NotEqual(GoneSourceText.HeldReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_held_row_that_carries_the_cleaned_source_does_not_keep_saying_gone_once_the_file_is_back()
    {
        // A file Weir cleaned and kept (the workflow keeps originals), then asked to process again, then lost for a while and returned.
        await SetUpAsync("movie", removeOriginal: false);
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        var info = new FileInfo(source);
        var modifiedNs = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
        await InsertHeldAsGoneRowAsync("Film.2024/Film.2024.mkv", size: info.Length, cleanedSource: $"{info.Length}, {modifiedNs}");

        await ScanAsync("movie");

        Assert.False(GoneSourceText.IsHeld(await StatusAsync("Film.2024/Film.2024.mkv") ?? string.Empty, await ReasonAsync("Film.2024/Film.2024.mkv")));
    }

    [Fact]
    public async Task A_cleaned_row_is_held_as_gone_then_goes_back_to_processed_without_a_second_entry()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/film.mkv", ProcessingFileStatuses.Unprocessed, size: 2000, lastSeenMinutesAgo: 1);
        await _fixture.Store.Execute("UPDATE files SET processed_source_size = 2000, processed_source_mtime_ns = 1700000000000000000 WHERE relative_path = 'Gone/film.mkv'");
        var sweep = Sweep();

        await sweep.RunOnceAsync(CancellationToken.None);
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Gone/film.mkv"));
        Assert.Equal(1, await LeftActivityAsync());

        Advance(11);
        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Equal("processed", await StatusAsync("Gone/film.mkv"));
        Assert.Equal(GoneSourceText.CleanedReason, await ReasonAsync("Gone/film.mkv"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files WHERE processed_source_size = 2000 AND processed_source_mtime_ns = 1700000000000000000 AND hold_until IS NULL"));
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task The_sweep_releases_a_held_file_that_is_back_which_a_scan_that_never_runs_would_not()
    {
        await SetUpAsync("movie");
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await InsertHeldAsGoneRowAsync("Film.2024/Film.2024.mkv", lastSeenMinutesAgo: 30);

        await Sweep().RunOnceAsync(CancellationToken.None);

        Assert.Equal("unprocessed", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(GoneSourceText.BackReason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Null(await HoldUntilAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task The_sweep_does_not_forget_a_file_that_comes_back_while_it_looks_again()
    {
        await SetUpAsync("movie");
        await InsertHeldAsGoneRowAsync("Film.2024/Film.2024.mkv", lastSeenMinutesAgo: 30);
        var looked = 0;

        await Sweep(_ =>
        {
            looked++;
            _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
            return Task.CompletedTask;
        }).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, looked);
        Assert.NotNull(await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_scan_does_not_wait_over_a_file_whose_pass_is_queued()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/queued.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: 1);
        var payload = $"{{\"relative_media_path\":\"Gone/queued.mkv\",\"media_scope\":\"movie\",\"library_id\":{_libraryId}}}";
        await _fixture.Jobs.EnqueueOrGetAsync($"queued-{Guid.NewGuid():N}", RemuxPassOutcomes.JobKind, payload);

        await ScanAsync("movie", _ => throw new InvalidOperationException("A file left to its pass is not looked at again."));

        Assert.Equal("unprocessed", await StatusAsync("Gone/queued.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task Nothing_is_held_as_gone_when_the_watched_folder_itself_is_missing()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/waiting.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: 1);
        Directory.Delete(_folders.Watched, recursive: true);

        await new VanishedFileSweepTask(_fixture.Store.Database, _fixture.Store.Options, _fixture.Libraries, _fixture.Store.Clock, NullLogger<VanishedFileSweepTask>.Instance) { GoneLookAgain = _ => Task.CompletedTask }
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal("unprocessed", await StatusAsync("Gone/waiting.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task The_sweep_says_a_gone_file_is_gone_once_and_forgets_it_later_without_saying_so_again()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/waiting.mkv", ProcessingFileStatuses.OnHold, lastSeenMinutesAgo: 1);
        var sweep = new VanishedFileSweepTask(_fixture.Store.Database, _fixture.Store.Options, _fixture.Libraries, _fixture.Store.Clock, NullLogger<VanishedFileSweepTask>.Instance) { GoneLookAgain = _ => Task.CompletedTask };

        await sweep.RunOnceAsync(CancellationToken.None);
        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync("Gone/waiting.mkv"));
        Assert.Equal(1, await LeftActivityAsync());

        Advance(11);
        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Null(await StatusAsync("Gone/waiting.mkv"));
        Assert.Equal(1, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_hand_off_row_no_scan_saw_is_forgotten_once_its_file_has_been_gone_a_while()
    {
        // #668: hand-off rows (recorded on receipt, never seen by a scan) and a failed row for a release folder must
        // not stay listed for good after their files are deleted.
        await SetUpAsync("movie");
        await InsertRowAsync("Film.1/Film.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: null);
        await InsertRowAsync("Film.2/Film.mkv", ProcessingFileStatuses.Cancelled, lastSeenMinutesAgo: null);
        await InsertRowAsync("Film", ProcessingFileStatuses.ProcessingFailed, lastSeenMinutesAgo: null, writtenMinutesAgo: 60 * 24 * 4);

        await ScanAsync("movie");

        Assert.Null(await StatusAsync("Film.1/Film.mkv"));
        Assert.Null(await StatusAsync("Film.2/Film.mkv"));
        Assert.Null(await StatusAsync("Film"));
        Assert.Equal(3, await LeftActivityAsync());
    }

    [Fact]
    public async Task The_sweep_forgets_gone_files_in_a_library_whose_scan_never_runs()
    {
        // #670: a library fed only by a media manager's hand-offs can have periodic scans off, so nothing would clear
        // rows the scan would have. The sweep runs the same rule on its own clock, without queueing any work.
        await SetUpAsync("movie");
        await InsertRowAsync("Film.1/Film.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: null);
        await InsertRowAsync("Film", ProcessingFileStatuses.ProcessingFailed, lastSeenMinutesAgo: null, writtenMinutesAgo: 60 * 24 * 4);
        _folders.Source(Path.Join("Still.Here", "Still.Here.mkv"));
        await InsertRowAsync("Still.Here/Still.Here.mkv", ProcessingFileStatuses.Unprocessed, lastSeenMinutesAgo: null);

        var log = new ListLogger<VanishedFileSweepTask>();
        await new VanishedFileSweepTask(_fixture.Store.Database, _fixture.Store.Options, _fixture.Libraries, _fixture.Store.Clock, log) { GoneLookAgain = _ => Task.CompletedTask }
            .RunOnceAsync(CancellationToken.None);

        Assert.Null(await StatusAsync("Film.1/Film.mkv"));
        Assert.Null(await StatusAsync("Film"));
        Assert.Equal("unprocessed", await StatusAsync("Still.Here/Still.Here.mkv"));
        Assert.Equal(2, await LeftActivityAsync());
        Assert.Equal(0, await RemuxJobsAsync());
        // Said in the server log too, for a machine where nobody signs in to read Activity.
        Assert.Contains(log.Entries, e => e.Message.Contains("Forgot 2 file(s) that left the watched folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_whose_path_is_a_folder_still_on_disk_is_kept()
    {
        await SetUpAsync("movie");
        Directory.CreateDirectory(Path.Join(_folders.Watched, "Release.Folder"));
        await InsertRowAsync("Release.Folder", ProcessingFileStatuses.ProcessingFailed, lastSeenMinutesAgo: null);

        await ScanAsync("movie");

        Assert.Equal("processing_failed", await StatusAsync("Release.Folder"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task A_file_with_a_pass_queued_is_left_to_that_pass()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/queued.mkv", ProcessingFileStatuses.Unprocessed);
        var payload = $"{{\"relative_media_path\":\"Gone/queued.mkv\",\"media_scope\":\"movie\",\"library_id\":{_libraryId}}}";
        await _fixture.Jobs.EnqueueOrGetAsync($"queued-{Guid.NewGuid():N}", RemuxPassOutcomes.JobKind, payload);

        await ScanAsync("movie");

        Assert.Equal("unprocessed", await StatusAsync("Gone/queued.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    [Fact]
    public async Task Nothing_is_forgotten_when_the_watched_folder_itself_is_missing()
    {
        await SetUpAsync("movie");
        await InsertRowAsync("Gone/waiting.mkv", ProcessingFileStatuses.Unprocessed);
        Directory.Delete(_folders.Watched, recursive: true);

        await ScanAsync("movie");

        Assert.Equal("unprocessed", await StatusAsync("Gone/waiting.mkv"));
        Assert.Equal(0, await LeftActivityAsync());
    }

    // --- #646 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_that_will_not_read_to_the_end_is_looked_at_again_later_not_on_every_scan()
    {
        await SetUpAsync("movie");
        _media.IntegrityError = "incomplete media data";
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync("movie");

        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(1, IntegrityReads());
        Assert.Equal(2, await RemuxJobsAsync());

        // Scans meanwhile change nothing: the next look is already queued, for later.
        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, IntegrityReads());
        Assert.Equal(2, await RemuxJobsAsync());
    }

    [Fact]
    public async Task A_file_waiting_for_its_next_look_stays_on_hold_until_then_and_keeps_saying_why()
    {
        await SetUpAsync("movie");
        _media.IntegrityError = "incomplete media data";
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync("movie");
        var reason = await ReasonAsync("Film.2024/Film.2024.mkv");
        var booked = await HoldUntilAsync("Film.2024/Film.2024.mkv");

        // The look is booked for five minutes from now, and the file says so.
        Assert.NotNull(booked);
        var expected = _fixture.Store.Clock.GetUtcNow().AddMinutes(RemuxPassHandler.UnreadableWaitMinutes[0]);
        Assert.InRange(booked.Value, expected.AddSeconds(-2), expected.AddSeconds(2));

        // Scans meanwhile find it present and settled. It is still not ready for the next free lane: calling it ready put it
        // first in line on Processing while a lane stood empty.
        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(reason, await ReasonAsync("Film.2024/Film.2024.mkv"));
        Assert.Equal(booked, await HoldUntilAsync("Film.2024/Film.2024.mkv"));
    }

    [Fact]
    public async Task A_file_that_never_reads_to_the_end_stops_being_waited_for_and_is_refused_not_handed_back()
    {
        await SetUpAsync("movie");
        _media.IntegrityError = "incomplete media data";
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync("movie");
        foreach (var minutes in RemuxPassHandler.UnreadableWaitMinutes)
        {
            Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
            Advance(minutes + 1);
            await DrainAsync();
        }

        Assert.Equal(RemuxPassHandler.UnreadableWaitMinutes.Count + 1, IntegrityReads());
        Assert.Equal("rejected", await StatusAsync("Film.2024/Film.2024.mkv"));
        Assert.Contains("It was refused before any output was written.", await ReasonAsync("Film.2024/Film.2024.mkv"), StringComparison.Ordinal);
        // Refused, not handed back: a file Weir cannot read is not one it can vouch for, whatever the failure policy says.
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
        Assert.False(File.Exists(Path.Join(_folders.Output, "Film.2024", "Film.2024.mkv")));
        Assert.True(File.Exists(Path.Join(_folders.Watched, "Film.2024", "Film.2024.mkv")));
    }

    // --- an unreadable file: waited for, never deleted ----------------------------------------------------------------

    private const string UnreadableProbe =
        "[mov,mp4,m4a,3gp,3g2,mj2 @ 000001b280590a00] moov atom not found\nC:\\Weir\\Ready\\Movies\\Film.2024.mkv: Invalid data found when processing input";

    [Fact]
    public async Task A_file_that_stays_unreadable_is_refused_after_the_looks_and_kept_even_when_the_workflow_deletes_rejected_files()
    {
        await SetUpAsync("movie");
        await _fixture.Store.Execute("UPDATE libraries SET rejected_file_action = 'delete_file'");
        _media.ProbeError = UnreadableProbe;
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        const string rel = "Film.2024/Film.2024.mkv";

        await ScanAndDrainAsync("movie");
        foreach (var minutes in RemuxPassHandler.UnreadableWaitMinutes)
        {
            Assert.Equal("on_hold", await StatusAsync(rel));
            Assert.True(File.Exists(source), "a file Weir cannot read yet is never deleted");
            Assert.Contains(ToolFailureText.NotReadableYet, await ReasonAsync(rel), StringComparison.Ordinal);
            Advance(minutes + 1);
            await DrainAsync();
        }

        Assert.Equal("rejected", await StatusAsync(rel));
        var reason = await ReasonAsync(rel);
        Assert.Contains("It was refused before any output was written.", reason, StringComparison.Ordinal);
        Assert.Contains(ToolFailureText.UnreadableKept, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("moov atom", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("@ 0", reason, StringComparison.Ordinal);
        Assert.True(File.Exists(source), "an unreadable file is refused, never deleted");
        Assert.False(File.Exists(Path.Join(_folders.Output, "Film.2024", "Film.2024.mkv")));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
    }

    [Fact]
    public async Task A_file_unreadable_at_first_is_processed_once_it_reads_and_is_not_deleted_meanwhile()
    {
        await SetUpAsync("movie");
        await _fixture.Store.Execute("UPDATE libraries SET rejected_file_action = 'delete_file'");
        _media.ProbeError = UnreadableProbe;
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        const string rel = "Film.2024/Film.2024.mkv";

        await ScanAndDrainAsync("movie");

        Assert.Equal("on_hold", await StatusAsync(rel));
        Assert.True(File.Exists(source));
        Assert.Empty(_media.Remuxes);

        // The copy finished: the file reads on the next look.
        _media.ProbeError = null;
        Advance(RemuxPassHandler.UnreadableWaitMinutes[0] + 1);
        await DrainAsync();

        Assert.Equal("processed", await StatusAsync(rel));
        Assert.True(File.Exists(Path.Join(_folders.Output, "Film.2024", "Film.2024.mkv")));
    }

    [Fact]
    public async Task A_reject_workflow_does_not_reject_a_file_on_its_first_unreadable_look()
    {
        await SetUpAsync("movie");
        await _fixture.Store.Execute("UPDATE libraries SET failure_policy = 'reject', retry_preflight_failures = 1");
        _media.ProbeError = UnreadableProbe;
        _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        const string rel = "Film.2024/Film.2024.mkv";

        await ScanAndDrainAsync("movie");

        Assert.Equal("on_hold", await StatusAsync(rel));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));

        _media.ProbeError = null;
        Advance(RemuxPassHandler.UnreadableWaitMinutes[0] + 1);
        await DrainAsync();

        Assert.Equal("processed", await StatusAsync(rel));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));
    }

    [Fact]
    public async Task A_file_that_is_still_growing_keeps_being_waited_for()
    {
        await SetUpAsync("movie");
        _media.IntegrityError = "incomplete media data";
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));

        await ScanAndDrainAsync("movie");
        for (var i = 0; i < RemuxPassHandler.UnreadableWaitMinutes.Count + 2; i++)
        {
            await File.AppendAllTextAsync(source, "more");
            Advance(60);
            await DrainAsync();
            Assert.Equal("on_hold", await StatusAsync("Film.2024/Film.2024.mkv"));
        }

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
    }

    // --- #643 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_file_is_left_alone_until_it_changes()
    {
        await SetUpAsync("movie");
        var source = _folders.Source(Path.Join("Film.2024", "Film.2024.mkv"));
        await InsertRowAsync("Film.2024/Film.2024.mkv", ProcessingFileStatuses.Cancelled, new FileInfo(source).Length);

        await ScanAndDrainAsync("movie");
        await ScanAndDrainAsync("movie");

        Assert.Equal(0, await RemuxJobsAsync());
        Assert.Equal("cancelled", await StatusAsync("Film.2024/Film.2024.mkv"));

        // A new download at the same path is a new file.
        File.WriteAllBytes(source, Enumerable.Repeat((byte)'y', 3000).ToArray());
        await ScanAndDrainAsync("movie");

        Assert.Equal(1, await RemuxJobsAsync());
    }
}
