using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Security;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Activity;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// Real-temp-dir, real-SQLite tests of the scan dispatch handler: it enqueues
/// <c>processing.file.remux_pass.v1</c> with the exact payload shape the remux pass reads, honours
/// <c>enqueue_remux_jobs</c>, skips a relative path already covered by an active pass, and applies a
/// library's rejected-file cleanup policy.
/// </summary>
public sealed class ProcessingWatchedFolderScanDispatchJobHandlerTests
{
    private static readonly LibraryStore Libraries = new();
    private static readonly FileStateStore Files = new();
    private static readonly FileSkipMarkerStore SkipMarkers = new();

    internal static async Task<(StoreFixture Store, ProcessingJobStore Jobs, ProcessingWatchedFolderScanDispatchJobHandler Handler)> BuildAsync(
        DataChangePublisher? changes = null)
    {
        var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "handler-tests-credentials-secret"));
        var cipher = new CredentialCipher(store.Options.CredentialsSecret, store.Options.SessionSecret, store.Options.PreviousCredentialsSecrets, store.Clock);
        var ports = new HttpMediaManagerPorts(new FakeManagerHttp());
        var connections = new MediaManagerConnectionService(store.Options, cipher, ports, new MediaManagerConnectionStore());
        var jobs = new ProcessingJobStore(store.Database, store.Clock);
        var handler = new ProcessingWatchedFolderScanDispatchJobHandler(
            store.Database, store.Clock, store.Options, jobs, connections, new SuiteSettingsStore(new AuthStore()), Libraries, Files, SkipMarkers, changes: changes)
        {
            GoneLookAgain = _ => Task.CompletedTask,
        };
        // Libraries these tests create wait for nothing and take any size (see CreateLibraryAsync), so they assert scan dispatch
        // itself, not the settling and hold-timer gates a freshly written test file would otherwise trip.
        await store.Execute(
            "INSERT INTO operator_settings (id, minimum_free_disk_space_mb) " +
            "VALUES (1, 0) ON CONFLICT(id) DO UPDATE SET minimum_free_disk_space_mb = 0");
        return (store, jobs, handler);
    }

    internal static async Task<long> CreateLibraryAsync(
        StoreFixture store, string watched, string output, bool enqueuePeriodicRemux = true, long readyAfterSeconds = 0,
        string rejectedFileAction = "leave", string excludePatternsCsv = "", bool removeOriginalAfterSuccess = true, long minFileSizeMb = 0)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var created = await Libraries.CreateAsync(uow, new ProcessingLibraryInput
        {
            Name = "Movies " + Guid.NewGuid().ToString("N")[..8],
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = watched,
            OutputFolder = output,
            ReadyAfterSeconds = readyAfterSeconds,
            MinFileSizeMb = minFileSizeMb,
            RejectedFileAction = rejectedFileAction,
            ExcludePatternsCsv = excludePatternsCsv,
            RemoveOriginalAfterSuccess = removeOriginalAfterSuccess,
        });
        await uow.CommitAsync();
        return created.Id;
    }

    internal static async Task RunScanAsync(ProcessingWatchedFolderScanDispatchJobHandler handler, ProcessingJobStore jobs, long libraryId, bool enqueueRemuxJobs)
    {
        var payload = new WireObject().Set("enqueue_remux_jobs", enqueueRemuxJobs).Set("scan_trigger", "manual").Set("media_scope", "movie").Set("library_id", libraryId);
        var job = await jobs.EnqueueOrGetAsync(
            $"scan-test-{Guid.NewGuid():N}",
            ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch,
            WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await handler.HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "test-owner"), CancellationToken.None);
    }

    [Fact]
    public async Task A_scan_of_a_workflow_with_no_watched_folder_fails_and_says_so()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var libraryId = await CreateLibraryAsync(store, store.Home.Join("watch"), store.Home.Join("out"));
        await store.Execute($"UPDATE libraries SET watched_folder = '' WHERE id = {libraryId}");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true));

        Assert.Contains("has no watched folder set", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_with_no_time_zone_saved_reads_its_schedule_in_UTC()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(store.Home.Join("out"));
        File.WriteAllBytes(Path.Combine(watched, "Gate Test 2001.mkv"), [1]);
        var libraryId = await CreateLibraryAsync(store, watched, store.Home.Join("out"));
        await store.Execute("UPDATE suite_settings SET app_timezone = ''");

        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.Equal(1, await store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task The_handler_enqueues_a_remux_pass_job_with_the_exact_payload_shape()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var mkv = Path.Combine(watched, "Gate Test 2001.mkv");
        File.WriteAllBytes(mkv, [1]);

        var libraryId = await CreateLibraryAsync(store, watched, output);
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxJobs = store.Database.Open();
        using var command = remuxJobs.CreateCommand();
        command.CommandText = "SELECT dedupe_key, status, payload_json FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var dedupeKey = reader.GetString(0);
        var status = reader.GetString(1);
        var payloadJson = reader.GetString(2);
        Assert.False(reader.Read());

        Assert.StartsWith("processing.file.remux_pass.v1:scan:", dedupeKey, StringComparison.Ordinal);
        Assert.Equal("pending", status);
        var body = (WireObject)WireJsonParser.Parse(payloadJson);
        Assert.Equal("Gate Test 2001.mkv", ((WireString)body.Get("relative_media_path")!).Value);
        Assert.Equal("movie", ((WireString)body.Get("media_scope")!).Value);
        Assert.Equal("manual", ((WireString)body.Get("trigger")!).Value);
        Assert.Equal(libraryId, (long)((WireInteger)body.Get("library_id")!).Value);
        Assert.Null(body.Get("dry_run"));
    }

    [Fact]
    public async Task A_look_that_finishes_tells_the_live_stream_even_when_it_finds_nothing()
    {
        var changes = new DataChangePublisher();
        using var published = new PublishedTopics(changes);
        var (store, jobs, handler) = await BuildAsync(changes);
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output);
        await published.TakeAsync();

        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.Contains(DataTopics.LibraryScan, await published.TakeAsync());
    }

    [Fact]
    public async Task Enqueue_remux_jobs_false_records_file_state_but_queues_nothing()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(watched, "Check Only 2001.mkv"), [1]);

        var libraryId = await CreateLibraryAsync(store, watched, output);
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: false);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(0, remuxCount);

        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var file = await Files.FindAsync(uow, libraryId, "Check Only 2001.mkv");
        Assert.NotNull(file);
        Assert.Equal(ProcessingFileStatuses.Unprocessed, file!.Status);
    }

    [Fact]
    public async Task A_relative_path_already_covered_by_an_active_pass_is_not_enqueued_twice()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(watched, "Already Queued 2001.mkv"), [1]);
        var libraryId = await CreateLibraryAsync(store, watched, output);

        var existingPayload = new WireObject().Set("relative_media_path", "Already Queued 2001.mkv").Set("media_scope", "movie").Set("library_id", libraryId);
        await jobs.EnqueueOrGetAsync("existing-remux", "processing.file.remux_pass.v1", WireJsonWriter.Dumps(existingPayload, WireJsonFormat.Compact));

        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(1, remuxCount);
    }

    [Fact]
    public async Task A_file_matching_the_library_exclude_pattern_under_delete_file_is_removed_and_recorded_skipped()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var sampleFolder = Path.Combine(watched, "Bad Release");
        Directory.CreateDirectory(sampleFolder);
        var sample = Path.Combine(sampleFolder, "sample.mkv");
        File.WriteAllBytes(sample, [1]);

        var libraryId = await CreateLibraryAsync(store, watched, output, rejectedFileAction: "delete_file", excludePatternsCsv: "*sample*");
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.False(File.Exists(sample));
        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(0, remuxCount);

        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var file = await Files.FindAsync(uow, libraryId, "Bad Release/sample.mkv");
        Assert.NotNull(file);
        Assert.Equal(ProcessingFileStatuses.Skipped, file!.Status);
    }

    /// <summary>Writes the Activity completion event the scan's own duplicate guard
    /// (<see cref="WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync"/>) reads, matching the exact
    /// source it names so the guard's fingerprint check passes.</summary>
    internal static async Task WriteCompletedPassEventAsync(StoreFixture store, long libraryId, string relativePath, string mediaScope, string source, string output)
    {
        var detail = $$"""
            {"ok":true,"relative_media_path":"{{relativePath}}","media_scope":"{{mediaScope}}","library_id":{{libraryId}},
            "output_file":"{{output.Replace("\\", "\\\\")}}","source_deleted_after_success":false,
            "inspected_source_path":"{{source.Replace("\\", "\\\\")}}","source_size_bytes":{{new FileInfo(source).Length}}}
            """.Replace("\n", string.Empty);
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        await uow.ExecuteAsync(
            "INSERT INTO activity_events (module, event_type, title, detail, relative_path, library_id) " +
            "VALUES ('processing', 'processing.file_remux_pass_completed', 'x', @detail, @path, @lib)",
            ("@detail", detail), ("@path", relativePath), ("@lib", libraryId));
        await uow.CommitAsync();
    }

    /// <summary>Forgets a file's row exactly as Activity's "Remove from list" endpoint does
    /// (<c>FileStateStore.ForgetAsync</c>): the row goes, nothing else does.</summary>
    private static async Task ForgetFileAsync(StoreFixture store, long fileId)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        await Files.ForgetAsync(uow, fileId);
        await uow.CommitAsync();
    }

    /// <summary>
    /// A finished file's own <c>files</c> row is not what stops a rescan from redoing it: the Activity event the
    /// pass wrote is. Forgetting the row, which is all "Remove from list" does, must not lose that guard —
    /// something deleting the event too would do.
    /// </summary>
    [Fact]
    public async Task A_finished_file_with_its_original_still_present_is_not_reprocessed_after_being_forgotten()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Kept Show S01E01.mkv");
        File.WriteAllBytes(source, "season one, episode one"u8.ToArray());
        var outputFile = Path.Combine(output, "Kept Show S01E01.mkv");
        File.WriteAllBytes(outputFile, "done"u8.ToArray());

        var libraryId = await CreateLibraryAsync(store, watched, output, removeOriginalAfterSuccess: false);
        long fileId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            fileId = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, last_seen_at) VALUES (@lib, @path, 'processed', @size, CURRENT_TIMESTAMP) RETURNING id",
                ("@lib", libraryId), ("@path", "Kept Show S01E01.mkv"), ("@size", new FileInfo(source).Length)));
            await uow.CommitAsync();
        }

        await WriteCompletedPassEventAsync(store, libraryId, "Kept Show S01E01.mkv", "movie", source, outputFile);
        await ForgetFileAsync(store, fileId);

        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(0, remuxCount);
    }

    /// <summary>
    /// A failed file's own row is the only thing that remembers it failed: unlike a finished pass, a failure writes
    /// no Activity event a rescan checks before queueing a file again. So forgetting the row (exactly what
    /// "Remove from list" does, and all it does) makes the next scan see a plain new candidate and queue it —
    /// true whether or not a job or Activity row for the old attempt still exists, since neither is consulted here.
    /// A person forgetting a failed file to let Weir try it again from scratch is the ordinary use of "Remove from
    /// list", so this is pinned as today's behaviour, not treated as something this fix owes a change. #785's "Keep"
    /// choice is the one exception: <see cref="A_kept_file_is_not_reprocessed_after_being_forgotten"/> below pins that.
    /// </summary>
    [Fact]
    public async Task A_failed_file_with_its_original_still_present_is_queued_again_after_being_forgotten()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Failed Movie 2001.mkv");
        File.WriteAllBytes(source, "not a real film"u8.ToArray());

        var libraryId = await CreateLibraryAsync(store, watched, output);
        long fileId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            fileId = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, last_seen_at) VALUES (@lib, @path, 'processing_failed', @size, CURRENT_TIMESTAMP) RETURNING id",
                ("@lib", libraryId), ("@path", "Failed Movie 2001.mkv"), ("@size", new FileInfo(source).Length)));
            await uow.CommitAsync();
        }

        await ForgetFileAsync(store, fileId);
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(1, remuxCount);
    }

    /// <summary>
    /// A rejected file behaves the same way as a failed one here (see the failed-file test above): its status lives
    /// only on the row "Remove from list" deletes, so a rescan re-admits it as a new candidate. Pinned for the same
    /// reason.
    /// </summary>
    [Fact]
    public async Task A_rejected_file_with_its_original_still_present_is_queued_again_after_being_forgotten()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Rejected Movie 2001.mkv");
        File.WriteAllBytes(source, "bad release"u8.ToArray());

        var libraryId = await CreateLibraryAsync(store, watched, output);
        long fileId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            fileId = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, last_seen_at) VALUES (@lib, @path, 'rejected', @size, CURRENT_TIMESTAMP) RETURNING id",
                ("@lib", libraryId), ("@path", "Rejected Movie 2001.mkv"), ("@size", new FileInfo(source).Length)));
            await uow.CommitAsync();
        }

        await ForgetFileAsync(store, fileId);
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(1, remuxCount);
    }

    /// <summary>
    /// "Keep, but don't process it again" (#785) changes the behaviour the two tests above pin: once a skip marker
    /// is recorded for this file's exact bytes, a rescan leaves it alone even though its row was forgotten the same
    /// way "Remove from list" forgets one.
    /// </summary>
    [Fact]
    public async Task A_kept_file_is_not_reprocessed_after_being_forgotten()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Kept Failed Movie 2001.mkv");
        File.WriteAllBytes(source, "not a real film"u8.ToArray());
        var fingerprint = SourceFiles.Fingerprint(source);

        var libraryId = await CreateLibraryAsync(store, watched, output);
        long fileId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            fileId = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, last_seen_at) VALUES (@lib, @path, 'processing_failed', @size, CURRENT_TIMESTAMP) RETURNING id",
                ("@lib", libraryId), ("@path", "Kept Failed Movie 2001.mkv"), ("@size", new FileInfo(source).Length)));
            await SkipMarkers.SetAsync(uow, libraryId, "Kept Failed Movie 2001.mkv", fingerprint.SizeBytes, fingerprint.ModifiedTimeNs);
            await uow.CommitAsync();
        }

        await ForgetFileAsync(store, fileId);
        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(0, remuxCount);
    }

    /// <summary>A kept file's skip marker is tied to its exact bytes: once the file changes, it is a new candidate.</summary>
    [Fact]
    public async Task A_kept_file_that_changes_is_reprocessed()
    {
        var (store, jobs, handler) = await BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Kept Then Changed Movie 2001.mkv");
        File.WriteAllBytes(source, "not a real film"u8.ToArray());
        var stale = SourceFiles.Fingerprint(source);

        var libraryId = await CreateLibraryAsync(store, watched, output);
        long fileId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            fileId = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, last_seen_at) VALUES (@lib, @path, 'processing_failed', @size, CURRENT_TIMESTAMP) RETURNING id",
                ("@lib", libraryId), ("@path", "Kept Then Changed Movie 2001.mkv"), ("@size", new FileInfo(source).Length)));
            await SkipMarkers.SetAsync(uow, libraryId, "Kept Then Changed Movie 2001.mkv", stale.SizeBytes, stale.ModifiedTimeNs);
            await uow.CommitAsync();
        }

        await ForgetFileAsync(store, fileId);
        File.WriteAllBytes(source, "a genuinely different release, longer than the first one"u8.ToArray());

        await RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var remuxCount = await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        Assert.Equal(1, remuxCount);
    }
}
