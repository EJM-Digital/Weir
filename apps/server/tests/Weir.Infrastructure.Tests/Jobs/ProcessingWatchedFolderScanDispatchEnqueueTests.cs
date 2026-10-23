using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Real-SQLite tests of enqueuing the periodic watched-folder scan dispatch job and its
/// prerequisite checks.</summary>
public sealed class ProcessingWatchedFolderScanDispatchEnqueueTests
{
    private static readonly LibraryStore Libraries = new();

    /// <summary>Inserts a library row directly, bypassing <see cref="LibraryRules.ValidateFolders"/> — the
    /// enqueue prerequisite checks under test are exactly what stands in for that validation for a scan,
    /// so tests need to reach states the store's own create/update guards would otherwise refuse.</summary>
    private static async Task<long> CreateLibraryAsync(JobsTestDatabase db, string name, string mediaType, string watched = "", string output = "")
    {
        db.Execute(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder) VALUES (@name, @type, @watched, @output)",
            ("@name", name), ("@type", mediaType), ("@watched", watched), ("@output", output));
        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        return (await Libraries.GetByNameAsync(uow, name))?.Id ?? throw new InvalidOperationException("Library insert failed.");
    }

    [Fact]
    public async Task Queue_has_active_scan_detects_pending_and_leased_rows_per_scope()
    {
        using var db = new JobsTestDatabase();

        db.Execute("INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts) VALUES ('wf-1', @kind, 'pending', 3)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch));
        await using (var uow = await UnitOfWork.OpenAsync(db.Database))
        {
            Assert.True(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", null));
            Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "tv", null));
        }

        db.Execute("DELETE FROM jobs");
        db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, payload_json) VALUES ('wf-tv', @kind, 'pending', 3, @payload)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch),
            ("@payload", "{\"media_scope\":\"tv\",\"scan_trigger\":\"manual\"}"));
        await using (var uow = await UnitOfWork.OpenAsync(db.Database))
        {
            Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", null));
            Assert.True(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "tv", null));
        }

        db.Execute("DELETE FROM jobs");
        db.Execute("INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, lease_owner, attempt_count) VALUES ('wf-leased', @kind, 'leased', 3, 'w', 1)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch));
        await using (var uow = await UnitOfWork.OpenAsync(db.Database))
        {
            Assert.True(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", null));
        }

        db.Execute("DELETE FROM jobs");
        db.Execute("INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, attempt_count) VALUES ('wf-done', @kind, 'completed', 3, 1)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch));
        await using (var uow = await UnitOfWork.OpenAsync(db.Database))
        {
            Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", null));
            Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "tv", null));
        }
    }

    [Fact]
    public async Task Queue_has_active_scan_treats_two_libraries_in_the_same_scope_independently()
    {
        using var db = new JobsTestDatabase();
        var libA = await CreateLibraryAsync(db, "Movies A", ProcessingMediaScopes.Movie);
        var libB = await CreateLibraryAsync(db, "Movies B", ProcessingMediaScopes.Movie);
        db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, payload_json) VALUES ('wf-libA', @kind, 'pending', 3, @payload)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch),
            ("@payload", $"{{\"media_scope\":\"movie\",\"library_id\":{libA}}}"));

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        Assert.True(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", libA));
        Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", libB));
    }

    [Fact]
    public async Task Validate_prerequisites_reports_no_saved_watched_folder_when_the_library_has_none()
    {
        using var db = new JobsTestDatabase();
        await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie, watched: "", output: "out");

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (ok, error) = await ProcessingWatchedFolderScanDispatchEnqueue.ValidatePrerequisitesAsync(uow, Libraries, enqueueRemuxJobs: false, "movie", null);

        Assert.False(ok);
        Assert.Equal(ScanDispatchPrerequisiteError.NoSavedWatchedFolder, error);
    }

    [Fact]
    public async Task Validate_prerequisites_requires_an_output_folder_only_when_enqueueing_remux_jobs()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var watched = dir.Join("watched");
        Directory.CreateDirectory(watched);
        await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie, watched: watched, output: "");

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (checkOnlyOk, _) = await ProcessingWatchedFolderScanDispatchEnqueue.ValidatePrerequisitesAsync(uow, Libraries, enqueueRemuxJobs: false, "movie", null);
        Assert.True(checkOnlyOk);

        var (liveOk, liveError) = await ProcessingWatchedFolderScanDispatchEnqueue.ValidatePrerequisitesAsync(uow, Libraries, enqueueRemuxJobs: true, "movie", null);
        Assert.False(liveOk);
        Assert.Equal(ScanDispatchPrerequisiteError.MissingOutputForLiveRemux, liveError);
    }

    [Fact]
    public async Task Enqueue_scan_dispatch_job_writes_the_exact_payload_shape_and_a_unique_dedupe_key()
    {
        using var db = new JobsTestDatabase();
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var job = await ProcessingWatchedFolderScanDispatchEnqueue.EnqueueScanDispatchJobAsync(uow, db.Store, enqueueRemuxJobs: true, "manual", "movie", 7);
        await uow.CommitAsync();

        Assert.Equal(ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, job.JobKind);
        Assert.StartsWith($"{ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch}:", job.DedupeKey, StringComparison.Ordinal);
        Assert.Equal(
            "{\"enqueue_remux_jobs\":true,\"scan_trigger\":\"manual\",\"media_scope\":\"movie\",\"library_id\":7}",
            job.PayloadJson);
    }

    [Fact]
    public async Task Enqueue_scan_dispatch_job_omits_library_id_when_none_is_given()
    {
        using var db = new JobsTestDatabase();
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        var job = await ProcessingWatchedFolderScanDispatchEnqueue.EnqueueScanDispatchJobAsync(uow, db.Store, enqueueRemuxJobs: false, "periodic", "tv", null);
        await uow.CommitAsync();

        Assert.Equal("{\"enqueue_remux_jobs\":false,\"scan_trigger\":\"periodic\",\"media_scope\":\"tv\"}", job.PayloadJson);
    }

    [Fact]
    public async Task Try_enqueue_periodic_skips_when_an_active_scan_already_covers_the_library()
    {
        using var db = new JobsTestDatabase();
        var libraryId = await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie);
        var library = await GetLibraryAsync(db, libraryId);
        db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, payload_json) VALUES ('wf-active', @kind, 'pending', 3, @payload)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch),
            ("@payload", $"{{\"media_scope\":\"movie\",\"library_id\":{libraryId}}}"));

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (inserted, skip) = await ProcessingWatchedFolderScanDispatchEnqueue.TryEnqueuePeriodicAsync(uow, db.Store, Libraries, library, enqueueRemuxJobs: true);

        Assert.False(inserted);
        Assert.StartsWith("active_scan_already_queued_", skip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Try_enqueue_periodic_inserts_when_prerequisites_are_met()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var watched = dir.Join("watched");
        var output = dir.Join("output");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie, watched, output);
        var library = await GetLibraryAsync(db, libraryId);

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (inserted, skip) = await ProcessingWatchedFolderScanDispatchEnqueue.TryEnqueuePeriodicAsync(uow, db.Store, Libraries, library, enqueueRemuxJobs: true);
        await uow.CommitAsync();

        Assert.True(inserted);
        Assert.Null(skip);
        var count = db.Count("SELECT COUNT(*) FROM jobs WHERE job_kind = @kind", ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch));
        Assert.Equal(1, count);
    }

    private static async Task<ProcessingLibraryRecord> GetLibraryAsync(JobsTestDatabase db, long id)
    {
        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        return await Libraries.GetAsync(uow, id) ?? throw new InvalidOperationException("Library not found.");
    }

    // --- the watcher's own enqueue path: identical apart from the trigger label ------------------------

    [Fact]
    public async Task Try_enqueue_for_watcher_event_inserts_with_the_filesystem_event_trigger()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var watched = dir.Join("watched");
        var output = dir.Join("output");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie, watched, output);
        var library = await GetLibraryAsync(db, libraryId);

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (inserted, skip) = await ProcessingWatchedFolderScanDispatchEnqueue.TryEnqueueForWatcherEventAsync(uow, db.Store, Libraries, library, enqueueRemuxJobs: true);
        await uow.CommitAsync();

        Assert.True(inserted);
        Assert.Null(skip);
        var job = await db.Store.GetAsync(1);
        Assert.Contains("\"scan_trigger\":\"filesystem_event\"", job?.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Try_enqueue_for_watcher_event_skips_when_an_active_scan_already_covers_the_library()
    {
        using var db = new JobsTestDatabase();
        var libraryId = await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie);
        var library = await GetLibraryAsync(db, libraryId);
        db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts, payload_json) VALUES ('wf-active', @kind, 'pending', 3, @payload)",
            ("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch),
            ("@payload", $"{{\"media_scope\":\"movie\",\"library_id\":{libraryId}}}"));

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (inserted, skip) = await ProcessingWatchedFolderScanDispatchEnqueue.TryEnqueueForWatcherEventAsync(uow, db.Store, Libraries, library, enqueueRemuxJobs: true);

        Assert.False(inserted);
        Assert.Equal("active_scan_already_queued", skip);
    }

    [Fact]
    public async Task Try_enqueue_for_watcher_event_skips_a_watched_folder_with_no_output_when_live_remux_is_on()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var watched = dir.Join("watched");
        Directory.CreateDirectory(watched);
        var libraryId = await CreateLibraryAsync(db, "Movies", ProcessingMediaScopes.Movie, watched, output: "");
        var library = await GetLibraryAsync(db, libraryId);

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var (inserted, skip) = await ProcessingWatchedFolderScanDispatchEnqueue.TryEnqueueForWatcherEventAsync(uow, db.Store, Libraries, library, enqueueRemuxJobs: true);

        Assert.False(inserted);
        Assert.Equal("missing_output_for_live_remux", skip);
    }

    [Fact]
    public async Task A_person_who_scans_is_told_nothing_when_there_is_no_workflow_to_check()
    {
        using var db = new JobsTestDatabase();

        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        Assert.Null(await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, Libraries, ProcessingMediaScopes.Movie, libraryId: null));
        Assert.Null(await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, Libraries, ProcessingMediaScopes.Movie, libraryId: 9999));
    }

    [Fact]
    public async Task A_person_who_scans_is_told_where_the_watched_folder_is_missing_and_nothing_when_it_is_there_or_unsaved()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var present = dir.Join("present");
        Directory.CreateDirectory(present);
        var presentId = await CreateLibraryAsync(db, "Present", ProcessingMediaScopes.Movie, present);
        var unsavedId = await CreateLibraryAsync(db, "Unsaved", ProcessingMediaScopes.Movie);
        var missing = dir.Join("missing");
        var missingId = await CreateLibraryAsync(db, "Missing", ProcessingMediaScopes.Movie, missing);

        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        Assert.Null(await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, Libraries, ProcessingMediaScopes.Movie, presentId));
        Assert.Null(await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, Libraries, ProcessingMediaScopes.Movie, unsavedId));
        Assert.StartsWith($"Weir can't see the watched folder {missing} for Missing.", await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, Libraries, ProcessingMediaScopes.Movie, missingId));
    }
}
