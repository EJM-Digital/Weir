using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Library;
using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Activity;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// #505 point 8: the clean job plans exactly as the scan did, then remuxes to a temp file beside the original and hands it
/// to the #506 safe swap. Refuses removal without confirmation, never touches the failure policy, and sorts behind
/// download-pipeline jobs.
/// </summary>
public sealed partial class LibraryCleanHandlerTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();
    private readonly TempDirectory _libraryFolder = new();
    private readonly FakeMediaRunner _media = new();
    private readonly LibrarySettingsStore _librarySettings = new();
    private readonly LibraryFileMarksStore _fileMarks = new();
    private readonly LibraryScanStore _scans = new();
    private readonly LibraryStore _libraries = new();

    public void Dispose()
    {
        _libraryFolder.Dispose();
        _fixture.Dispose();
    }

    private LibraryCleanHandler Handler(
        IHardlinkInspector? hardlinks = null, ILibraryFileChangeNotifier? notifier = null, IRemovedTrackStore? removedTracks = null, ILogger<LibraryCleanHandler>? logger = null) =>
        (LibraryCleanHandler)Activator.CreateInstance(typeof(LibraryCleanHandler), HandlerArguments(hardlinks, notifier, removedTracks, logger))!;

    /// <summary>What the handler is built from, in the order of its constructor.</summary>
    private object[] HandlerArguments(
        IHardlinkInspector? hardlinks = null, ILibraryFileChangeNotifier? notifier = null, IRemovedTrackStore? removedTracks = null, ILogger<LibraryCleanHandler>? logger = null)
    {
        var tools = new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System);
        var swap = new SafeSwap(
            PhysicalSwapFileSystem.Instance,
            new ProcessingJobSwapJournal(_fixture.Store.Database),
            new RemuxOutputSwapValidator(tools),
            NullLogger<SafeSwap>.Instance);
        // No manager connections are configured in this fixture, so the real notifier finds nothing that owns
        // the changed path and makes no HTTP call — equivalent to a no-op for these tests, but exercising the
        // same #507 code the running server does.
        notifier ??= new LibraryFileChangeNotifier(
            _fixture.Connections, _fixture.Store.Database, new SqliteActivityWriter(_fixture.Store.Database), _fixture.Store.Clock, delay: (_, _) => Task.CompletedTask);
        removedTracks ??= new Weir.Infrastructure.Library.FileLogRemovedTrackStore(_fixture.Store.Database, _fixture.Store.Clock);
        return
        [
            _fixture.Store.Database, tools, swap, notifier, hardlinks ?? PhysicalHardlinkInspector.Instance, removedTracks,
            _librarySettings, _fileMarks, _scans, _fixture.Jobs, _libraries, _fixture.Changes, _fixture.Store.Clock, logger ?? NullLogger<LibraryCleanHandler>.Instance,
        ];
    }

    /// <summary>A library whose rule set keeps only English audio, strictly — the same policy proven in
    /// <c>LibraryFilePlannerTests</c> to drop a Japanese track.</summary>
    private async Task<long> LibraryAsync()
    {
        var ruleSetId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO rule_sets (name, primary_audio_lang, audio_preference_mode) " +
            "VALUES ('English only', 'eng', 'preferred_langs_strict') RETURNING id")), CultureInfo.InvariantCulture);
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order, rule_set_id) " +
            "VALUES ('Movies library', 'movie', '/downloads/watched', '/downloads/output', '/downloads/work', 1, $rule_set_id) RETURNING id",
            ("$rule_set_id", ruleSetId))), CultureInfo.InvariantCulture);
    }

    private Task<long> EnqueueCleanAsync(long libraryId, string path, bool confirmFinalRemoval) =>
        _fixture.Store.WithUnitOfWork(async uow =>
            (await _scans.EnqueueCleanAsync(uow, _fixture.Jobs, libraryId, path, "manual", confirmFinalRemoval)).Id);

    private Task<long> EnqueueChosenCleanAsync(long libraryId, string path, ManualPlanChoice choice, long? expectedSizeBytes = null) =>
        _fixture.Store.WithUnitOfWork(async uow =>
            (await _scans.EnqueueCleanAsync(
                uow, _fixture.Jobs, libraryId, path, "manual", true, ManualPlanJson.ToPyDict(choice), expectedSizeBytes)).Id);

    /// <summary>Keep exactly these input indices, in this order: the video first, and the track after it as default.</summary>
    private static ManualPlanChoice Keeping(params int[] indices) =>
        new(indices.Select((index, position) => new ManualKeepEntry(index, Default: position == 1, Forced: false)).ToList(), indices);

    /// <summary>Claims, runs and completes one clean job exactly as the real worker would.</summary>
    private async Task RunCleanAsync(long jobId, LibraryCleanHandler? handler = null)
    {
        const string leaseOwner = "test-worker";
        var claimed = await _fixture.Jobs.ClaimNextAsync(leaseOwner, _fixture.Store.Clock.GetUtcNow().AddMinutes(5), _fixture.Store.Clock.GetUtcNow());
        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed!.Id);
        await (handler ?? Handler()).HandleAsync(new JobWorkContext(claimed.Id, claimed.JobKind, claimed.PayloadJson, leaseOwner), CancellationToken.None);
        await _fixture.Jobs.CompleteClaimedAsync(claimed.Id, leaseOwner);
    }

    private Task<int> UpdateLibraryAsync(long libraryId, string assignment) =>
        _fixture.Db(async uow =>
        {
            await uow.ExecuteAsync($"UPDATE libraries SET {assignment} WHERE id = $id", ("$id", libraryId));
            return 0;
        });

    private Task<string?> CleanedDetailAsync() =>
        _fixture.Db(async uow => Convert.ToString(
            await uow.ScalarAsync($"SELECT title FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"),
            CultureInfo.InvariantCulture));

    private Task<int> ReferencePolicyJobCountAsync() =>
        _fixture.Store.Scalar(
                $"SELECT count(*) FROM jobs WHERE job_kind IN ('processing.file.reject.v1', 'processing.file.pass_through.v1')")
            .ContinueWith(t => (int)t.Result, TaskScheduler.Default);

    [Fact]
    public async Task Cleaning_a_file_that_would_remove_tracks_is_refused_without_confirmation()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var before = await File.ReadAllBytesAsync(path);

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: false);
        await RunCleanAsync(jobId);

        // Refused: the original is untouched, and only Activity's "failed" event was written — no swap ran.
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(SafeSwapRules.TempPath(path)));
        Assert.False(File.Exists(SafeSwapRules.BackupPath(path)));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"));
    }

    [Fact]
    public async Task A_library_failure_never_queues_a_reject_or_pass_through_job()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("unreadable.mkv");
        await File.WriteAllBytesAsync(path, [9, 9, 9]);
        _media.ProbeError = "moov atom not found";

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        Assert.Equal(0, await ReferencePolicyJobCountAsync());
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"));
    }

    [Fact]
    public async Task Library_jobs_are_claimed_behind_a_pending_download_job()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        // A library job enqueued first, then a download job enqueued after: priority still decides, not order.
        await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await _fixture.Jobs.EnqueueOrGetAsync("download:1", RemuxPassOutcomes.JobKind, "{}");

        var first = await _fixture.Jobs.ClaimNextAsync("w1", _fixture.Store.Clock.GetUtcNow().AddMinutes(5), _fixture.Store.Clock.GetUtcNow());
        Assert.NotNull(first);
        Assert.Equal(RemuxPassOutcomes.JobKind, first!.JobKind);

        var second = await _fixture.Jobs.ClaimNextAsync("w2", _fixture.Store.Clock.GetUtcNow().AddMinutes(5), _fixture.Store.Clock.GetUtcNow());
        Assert.NotNull(second);
        Assert.Equal(LibraryModeJobKinds.CleanKind, second!.JobKind);
    }

    [Fact]
    public async Task A_file_with_no_matching_manager_still_processes()
    {
        // No manager connections are linked to the library, so the file is unmatched — #505 says that must not block it.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
        Assert.False(File.Exists(path + ".weir-bak.mkv"));
    }

    [Fact]
    public async Task A_clean_probes_the_original_once()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
        Assert.Single(_media.Probed, argv => argv[^1] == path);
    }

    [Fact]
    public async Task An_end_to_end_clean_replaces_the_file_in_place_and_leaves_no_leftovers()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        var originalBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await File.WriteAllBytesAsync(path, originalBytes);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        // Committed: the file at the original name now holds the cleaned (fake) output, and no temp/backup remains.
        Assert.True(File.Exists(path));
        Assert.NotEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(SafeSwapRules.TempPath(path)));
        Assert.False(File.Exists(SafeSwapRules.BackupPath(path)));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
        Assert.Equal(0, await ReferencePolicyJobCountAsync());
    }

    [Fact]
    public async Task A_workflow_that_prefers_mkvmerge_says_so_when_ffmpeg_had_to_write_the_file()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));

        Assert.Contains("Weir wrote it with ffmpeg because mkvmerge could not.", await CleanedDetailAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_set_to_ffmpeg_cleans_with_ffmpeg_and_has_nothing_to_explain()
    {
        var library = await LibraryAsync();
        await UpdateLibraryAsync(library, "remux_writer = 'ffmpeg'");
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));

        Assert.Single(_media.Remuxes);
        Assert.DoesNotContain("mkvmerge", await CleanedDetailAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_workflows_ffmpeg_compatibility_applies_when_cleaning_a_library_file()
    {
        var library = await LibraryAsync();
        await UpdateLibraryAsync(library, "ffmpeg_strictness = 'experimental'");
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));

        var remux = Assert.Single(_media.Remuxes);
        Assert.Contains("experimental", remux.TakeWhile(argument => argument != "-i"));
    }

    [Fact]
    public async Task Keeping_the_original_puts_it_in_dot_weir_originals_and_still_cleans_the_file()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow =>
        {
            await _librarySettings.SetAsync(
                uow, library, new LibrarySettings([_libraryFolder.Path], ScheduleEnabled: false, KeepOriginalAfterClean: true));
            return 0;
        });
        var path = _libraryFolder.Join("film.mkv");
        var originalBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await File.WriteAllBytesAsync(path, originalBytes);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        var kept = Path.Combine(_libraryFolder.Path, ".weir-originals", "film.mkv");
        Assert.True(File.Exists(path));
        Assert.NotEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(kept));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(kept));
        Assert.False(File.Exists(SafeSwapRules.TempPath(path)));
        Assert.False(File.Exists(SafeSwapRules.BackupPath(path)));

        var title = await _fixture.Db(async uow => Convert.ToString(
            await uow.ScalarAsync($"SELECT title FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"),
            CultureInfo.InvariantCulture));
        Assert.Contains($"The original was kept at {kept}", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cleaned_copy_that_fails_the_staged_output_check_is_never_swapped_in()
    {
        // #500: the copy still carries the Japanese track the plan drops, so the staged-output check rejects it before
        // the safe swap starts. The library lane runs the same check the download pass does, inside the remux step.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        var originalBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await File.WriteAllBytesAsync(path, originalBytes);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(SafeSwapRules.TempPath(path)));
        Assert.False(File.Exists(SafeSwapRules.BackupPath(path)));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"));
        var title = await _fixture.Db(async uow => Convert.ToString(
            await uow.ScalarAsync($"SELECT title FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"),
            CultureInfo.InvariantCulture));
        Assert.Contains("while writing the cleaned copy", title, StringComparison.Ordinal);
        Assert.Contains("Planned 1 audio track, output has 2", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_clean_does_not_stop_the_same_file_being_cleaned_again()
    {
        // The dedupe key means "one clean outstanding for this file", not "this file has had its turn": a rule change,
        // or a hand-picked plan, must be able to clean a file that was already cleaned days ago.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var first = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(first);
        var second = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);

        // SQLite hands the deleted row's id straight back, so the id proves nothing: what matters is that the job
        // this second request returned is one waiting to run, not the finished one from before.
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE id = {second} AND status = '{ProcessingJobStatus.Pending}'"));
    }

    [Fact]
    public async Task A_chosen_plan_is_used_instead_of_the_librarys_rules()
    {
        // The library keeps English and drops Japanese. Someone who wants the opposite for one film says so, and
        // that is what the clean does.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.JapaneseOnly;

        var jobId = await EnqueueChosenCleanAsync(library, path, Keeping(0, 2));
        await RunCleanAsync(jobId);

        var remux = Assert.Single(_media.Remuxes);
        Assert.Contains("0:2", remux);
        Assert.DoesNotContain("0:1", remux);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
    }

    [Fact]
    public async Task A_file_that_changed_since_its_tracks_were_chosen_is_left_alone()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        // Chosen against a file of a different size: the indices in the choice describe some other file's tracks.
        var jobId = await EnqueueChosenCleanAsync(library, path, Keeping(0, 2), expectedSizeBytes: 999);
        await RunCleanAsync(jobId);

        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"));
    }

    [Fact]
    public async Task A_file_you_asked_Weir_to_leave_alone_is_not_cleaned()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await _fixture.Db(async uow =>
        {
            await _fileMarks.SetLeaveAloneAsync(uow, library, path, leaveAlone: true, _fixture.Store.Clock.GetUtcNow());
            return 0;
        });
        await RunCleanAsync(jobId);

        // Set aside after the job was queued: the handler is the last word, so the file is still untouched.
        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileSkipped}'"));
    }

    [Fact]
    public async Task A_clean_records_that_the_file_has_been_cleaned()
    {
        // The next scan rewrites the file list from scratch, so "Weir has cleaned this one" has to be kept elsewhere.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        await RunCleanAsync(jobId);

        var mark = await _fixture.Db(uow => _fileMarks.FindAsync(uow, library, path));
        Assert.NotNull(mark);
        Assert.NotNull(mark!.CleanedAt);
        Assert.False(mark.LeaveAlone);
    }

    private Task<long> PendingScansAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{LibraryModeJobKinds.ScanKind}' AND status = 'pending'");

    [Fact]
    public async Task A_clean_that_changed_a_file_asks_for_the_library_to_be_scanned_again_so_the_file_reads_as_clean()
    {
        // The file list is only as new as the last scan, so without a scan the file would go on reading as needing this very clean.
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        Assert.Equal(0, await PendingScansAsync());

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));

        Assert.Equal(1, await PendingScansAsync());
        var payload = await _fixture.Db(async uow => Convert.ToString(
            await uow.ScalarAsync($"SELECT payload_json FROM jobs WHERE job_kind = '{LibraryModeJobKinds.ScanKind}'"), CultureInfo.InvariantCulture));
        Assert.Contains($"\"library_id\":{library}", payload, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"worker\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleans_that_finish_while_a_scan_is_already_waiting_ask_for_no_second_one()
    {
        var library = await LibraryAsync();
        _media.Probes["first.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.Probes["second.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var queued = new List<long>();
        foreach (var name in new[] { "first.mkv", "second.mkv" })
        {
            var path = _libraryFolder.Join(name);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            queued.Add(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));
        }

        foreach (var jobId in queued)
        {
            await RunCleanAsync(jobId);
        }

        Assert.Equal(1, await PendingScansAsync());
    }

    [Fact]
    public async Task A_clean_that_changed_nothing_asks_for_no_scan()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: false));

        Assert.Equal(0, await PendingScansAsync());
    }

    /// <summary>More than any drive has, so the free-space check always finds the drive short.</summary>
    private const long MoreFreeThanAnyDriveHasMb = 1_000_000_000;

    private Task KeepMoreFreeThanTheDriveHasAsync(long library) =>
        _fixture.Store.Execute($"UPDATE libraries SET minimum_free_disk_space_mb = {MoreFreeThanAnyDriveHasMb} WHERE id = {library}");

    [Fact]
    public async Task A_clean_waits_for_room_when_the_workflow_keeps_more_free_than_the_drive_has()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await KeepMoreFreeThanTheDriveHasAsync(library);
        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        using var published = new PublishedTopics(_fixture.Changes);

        await RunCleanAsync(jobId);

        Assert.Contains(DataTopics.Jobs, await published.TakeAsync());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(_media.Remuxes);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE id = {jobId} AND status = '{ProcessingJobStatus.Pending}' AND not_before IS NOT NULL"));
        var reason = await _fixture.Db(uow => uow.QuerySingleAsync(
            $"SELECT title FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileSkipped}'", reader => reader.GetString(0)));
        Assert.StartsWith("Waiting: the drive this file is on has less than", reason, StringComparison.Ordinal);
        Assert.EndsWith("Weir tries again when there is room.", reason, StringComparison.Ordinal);
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileFailed}'"));
    }

    [Fact]
    public async Task A_clean_waiting_for_room_is_looked_at_again_further_apart_and_never_gives_up()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await KeepMoreFreeThanTheDriveHasAsync(library);
        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        var looksAt = new List<TimeSpan>();

        foreach (var _ in DiskSpaceWaits.LookMinutes.Append(DiskSpaceWaits.LookMinutes[^1]))
        {
            var lookedAt = _fixture.Store.Clock.GetUtcNow();
            await RunCleanAsync(jobId);
            var booked = TimestampColumns.Parse(await _fixture.Db(uow => uow.ScalarAsync($"SELECT not_before FROM jobs WHERE id = {jobId}")))!.Value;
            looksAt.Add(booked - lookedAt);
            _fixture.Store.Clock.SetUtcNow(booked);
        }

        Assert.Equal([10, 30, 60, 60], looksAt.Select(gap => (int)gap.TotalMinutes));
    }

    [Theory]
    [InlineData(1, 0, "removed 1 audio track")]
    [InlineData(2, 0, "removed 2 audio tracks")]
    [InlineData(0, 1, "removed 1 subtitle track")]
    [InlineData(1, 3, "removed 1 audio track and 3 subtitle tracks")]
    [InlineData(0, 0, "removed no audio or subtitle tracks")]
    public void What_a_clean_removed_reads_in_english(int audio, int subtitles, string expected) =>
        Assert.Equal(expected, LibraryCleanHandler.RemovedTracks(audio, subtitles));
}
