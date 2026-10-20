using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The remux pass handler's activity reporting, plus the policy and hand-off paths the handler drives: a real database at head, real folders, and ffprobe/ffmpeg behind a fake process runner.
/// </summary>
public sealed partial class RemuxPassHandlerTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();

    public RemuxPassHandlerTests()
    {
        _fixture.Store.Execute(
            "UPDATE operator_settings SET minimum_free_disk_space_mb = 0")
            .GetAwaiter().GetResult();
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
        _fixture.DelunoListsNoLibraries();
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private RemuxPassHandler Handler(IFailurePolicy? policy = null, Func<string, long>? freeBytes = null, ScanWakeups? wakeups = null)
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
            NullLogger<RemuxPassRunner>.Instance)
        {
            FreeBytes = freeBytes,
        };
        return new RemuxPassHandler(
            _fixture.Store.Database,
            _fixture.Store.Options,
            runner,
            policy ?? new QueueingFailurePolicy(_fixture.Jobs),
            _fixture.OperatorSettings,
            _fixture.Handback,
            _fixture.Libraries,
            TimeProvider.System,
            NullLogger<RemuxPassHandler>.Instance,
            new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance),
            _fixture.Reporter,
            scanWakeups: wakeups,
            jobs: _fixture.Jobs);
    }

    private async Task<long> LibraryAsync(string failurePolicy = "pass_through", long maxAttempts = 3, string rejectedFileAction = "leave", string mediaType = "movie", long readyAfterSeconds = 0)
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, display_order, min_file_size_mb, ready_after_seconds) " +
            "VALUES ('Movies', $type, $w, $o, $k, $policy, $max, $action, 60, 1, 0, $ready) RETURNING id",
            ("$type", mediaType),
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$policy", failurePolicy),
            ("$max", maxAttempts),
            ("$action", rejectedFileAction),
            ("$ready", readyAfterSeconds))), CultureInfo.InvariantCulture);
    }

    private Task FileRowAsync(long libraryId, string relativePath, string status = "processing") =>
        _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({libraryId}, '{relativePath}', '{status}', 'Processing')");

    private async Task<long> EnqueueAsync(string payload, string dedupe)
    {
        var job = await _fixture.Jobs.EnqueueOrGetAsync(dedupe, RemuxPassOutcomes.JobKind, payload);
        return job.Id;
    }

    private static JobWorkContext Context(long id, string payload) => new(id, RemuxPassOutcomes.JobKind, payload, "test");

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task The_progress_row_becomes_the_completed_activity_row_and_the_file_is_processed()
    {
        var library = await LibraryAsync();
        _folders.Source(Path.Join("Movie", "file.mkv"));
        _media.Probes["file.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await FileRowAsync(library, "Movie/file.mkv", "unprocessed");
        var payload = $$$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{{library}}}}""";

        await Handler().HandleAsync(Context(123, payload), CancellationToken.None);

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
        Assert.Equal("processing.file_remux_pass_completed|processing|file.mkv was processed successfully", await ScalarText("SELECT event_type || '|' || module || '|' || title FROM activity_events"));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events"));
        Assert.Equal(("live_output_written", "123"), (WireConvert.Str(detail["outcome"]), WireConvert.Str(detail["job_id"])));
        Assert.Equal("processed|Finished processing this file.", await ScalarText("SELECT status || '|' || status_reason FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM file_logs WHERE outcome = 'live_output_written' AND library_name = 'Movies' AND file_id IS NOT NULL"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files WHERE video_codec = 'h264' AND audio_track_count = 2 AND output_collision_action = 'write'"));
    }

    [Fact]
    public async Task Claiming_a_file_tells_open_pages_before_any_work_on_it_begins()
    {
        // Checking and planning write no Activity row, so the claim is the only thing that can tell a Processing page the file
        // has left Waiting until the write starts.
        var library = await LibraryAsync();
        _folders.Source(Path.Join("Movie", "file.mkv"));
        _media.Probes["file.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await FileRowAsync(library, "Movie/file.mkv", "unprocessed");
        var payload = $$$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{{library}}}}""";
        var notifier = ActivityNotifications.For(_fixture.Store.Database);
        var versionBefore = notifier.Snapshot().Version;
        long? versionAtFirstWork = null;
        _media.OnCall = () => versionAtFirstWork ??= notifier.Snapshot().Version;

        await Handler().HandleAsync(Context(124, payload), CancellationToken.None);

        Assert.True(versionAtFirstWork > versionBefore);
    }

    [Fact]
    public async Task Media_facts_and_collision_writes_are_scoped_by_library_id()
    {
        // Matching measured-media-facts and output-collision writes on relative_path alone would update two
        // libraries that happen to share a path. Only the pass's own library's row should change.
        var library = await LibraryAsync();
        var otherLibraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, display_order) VALUES ('Other', 'movie', '', 99) RETURNING id")), CultureInfo.InvariantCulture);
        _folders.Source(Path.Join("Movie", "file.mkv"));
        _media.Probes["file.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        await FileRowAsync(library, "Movie/file.mkv", "unprocessed");
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status) VALUES ({otherLibraryId}, 'Movie/file.mkv', 'unprocessed')");
        var payload = $$$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{{library}}}}""";

        await Handler().HandleAsync(Context(200, payload), CancellationToken.None);

        Assert.Equal(
            1,
            await _fixture.Store.Scalar($"SELECT count(*) FROM files WHERE library_id = {library} AND video_codec = 'h264' AND audio_track_count = 2 AND output_collision_action = 'write'"));
        Assert.Equal(
            1,
            await _fixture.Store.Scalar($"SELECT count(*) FROM files WHERE library_id = {otherLibraryId} AND video_codec IS NULL AND output_collision_action IS NULL"));
    }

    [Fact]
    public async Task A_source_that_is_not_ready_puts_the_file_on_hold_without_counting_a_failure()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, failure_class, failure_attempts) VALUES ({library}, 'Testament/file.mkv', 'processing', 'execution', 2)");
        var result = new WireObject().Set("ok", false).Set("outcome", "source_not_ready").Set("retryable_wait", true)
            .Set("relative_media_path", "Testament/file.mkv").Set("reason", "This file is still open for writing by another program.");

        await Handler().ApplyFileOutcomeStateAsync(result, library, "movie", null);

        Assert.Equal("on_hold|0||", await ScalarText("SELECT status || '|' || failure_attempts || '|' || coalesce(failure_class, '') || '|' || coalesce(next_retry_at, '') FROM files"));
        Assert.False(((WireBool)result["retry_scheduled"]).Value);
    }

    [Fact]
    public async Task A_handed_off_file_that_is_too_young_is_looked_at_again_instead_of_failing()
    {
        // A media manager hands a file over within seconds of the download finishing, inside the workflow's wait.
        // Failing the pre-check there would skip the retry and run the failure policy: every such file would be passed
        // through unprocessed, and under "reject" a good release would be reported bad.
        var library = await LibraryAsync(failurePolicy: "reject", readyAfterSeconds: 60);
        var source = _folders.Source(Path.Join("Film", "film.mkv"));
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.AddConnectionAsync("native", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "native", "h632", library, "Film/film.mkv"); return 0; });
        var handoff = $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","trigger":"webhook","library_id":{{{library}}},"origin":{"source_key":"native","handoff_id":"h632","callback_path":"{{{EventsPath}}}","release_name":"Film.2001"}}""";
        var first = await EnqueueAsync(handoff, "remux:h632");
        var before = DateTimeOffset.UtcNow;

        await Handler().HandleAsync(Context(first, handoff), CancellationToken.None);

        // On hold with a reason that says how long; no failure counted, no policy run, and the manager told nothing yet.
        Assert.Equal("on_hold|0|", await ScalarText("SELECT status || '|' || failure_attempts || '|' || coalesce(failure_class, '') FROM files"));
        Assert.Contains("changed too recently", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind <> 'processing.file.remux_pass.v1'"));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        Assert.True(File.Exists(source));

        // The second look is a job held back until the file is old enough, carrying the hand-off's origin.
        var secondId = await _fixture.Store.Scalar($"SELECT id FROM jobs WHERE id <> {first}");
        var secondPayload = await ScalarText($"SELECT payload_json FROM jobs WHERE id = {secondId}");
        var second = (WireObject)WireJsonParser.Parse(secondPayload);
        Assert.Equal(1, (long)((WireInteger)second["minimum_age_waits"]).Value);
        Assert.Equal("h632", WireConvert.Str(((WireObject)second["origin"])["handoff_id"]));
        var notBefore = DateTimeOffset.Parse(await ScalarText($"SELECT not_before FROM jobs WHERE id = {secondId}"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(notBefore, before.AddSeconds(50), before.AddSeconds(70));

        // Old enough now: the second look processes the file and reports the hand-off as completed.
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        await Handler().HandleAsync(Context(secondId, secondPayload), CancellationToken.None);

        Assert.Equal("processed", await ScalarText("SELECT status FROM files"));
        Assert.Equal(2, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task A_file_that_never_stops_changing_is_not_looked_at_forever()
    {
        var library = await LibraryAsync(readyAfterSeconds: 60);
        _folders.Source(Path.Join("Film", "film.mkv"));
        await FileRowAsync(library, "Film/film.mkv");
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","library_id":{{{library}}},"minimum_age_waits":{{{RemuxPassHandler.MaxMinimumAgeWaits}}}}""";
        var id = await EnqueueAsync(payload, "remux:still-changing");

        await Handler().HandleAsync(Context(id, payload), CancellationToken.None);

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
        Assert.StartsWith("on_hold|", await ScalarText("SELECT status || '|' FROM files"), StringComparison.Ordinal);
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
        Assert.Contains("stopped looking", WireConvert.Str(detail["reason"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_no_video_file_is_deleted_only_after_the_rejection_was_recorded()
    {
        var library = await LibraryAsync(rejectedFileAction: "delete_file");
        var source = _folders.Source("audio-only.mpg");
        _media.DefaultProbe = """{"streams":[{"index":0,"codec_type":"audio","codec_name":"ac3","channels":2}]}""";
        await FileRowAsync(library, "audio-only.mpg");

        await Handler().HandleAsync(Context(5, $$$"""{"relative_media_path":"audio-only.mpg","library_id":{{{library}}}}"""), CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.StartsWith("rejected|0|", await ScalarText("SELECT status || '|' || failure_attempts || '|' || status_reason FROM files"), StringComparison.Ordinal);
        Assert.EndsWith(WeirOnlyRejection.Deleted, await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM file_logs WHERE title = 'Rejected file cleanup finished'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind <> 'processing.file.remux_pass.v1'"));
    }

    private const string ForeignOnlyReason =
        "Rejected: none of its audio tracks are in English, and the \"English only\" rules keep only English audio, so there would be nothing to keep. " +
        "It has Japanese audio. To accept files like this, change the first-choice language or \"How to choose audio\" in Setup › Rules › Profiles.";

    /// <summary>Gives the library a rules profile that keeps English audio only, so a Japanese-only file has nothing to keep.</summary>
    private async Task EnglishOnlyRulesAsync(long libraryId)
    {
        var ruleSetId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO rule_sets (name, primary_audio_lang, audio_preference_mode) VALUES ('English only', 'eng', 'preferred_langs_strict') RETURNING id")), CultureInfo.InvariantCulture);
        await _fixture.Store.Execute($"UPDATE libraries SET rule_set_id = {ruleSetId} WHERE id = {libraryId}");
    }

    private string ForeignFile => Path.Join(_folders.Watched, "Foreign Again (2020).mkv");

    private HistoryFileRemovalService Removal() =>
        new(_fixture.Ports, _fixture.Connections, _fixture.Reporter, new RejectRoutes(_fixture.Ports, _fixture.Reporter), _fixture.Libraries, _fixture.Files, new FileSkipMarkerStore());

    private async Task<ProcessingFileRecord> RejectedForeignFileAsync(string failurePolicy = "pass_through", string rejectedFileAction = "leave")
    {
        var library = await LibraryAsync(failurePolicy, rejectedFileAction: rejectedFileAction);
        await EnglishOnlyRulesAsync(library);
        _folders.Source("Foreign Again (2020).mkv");
        _media.DefaultProbe = FakeMediaRunner.JapaneseOnly;
        await FileRowAsync(library, "Foreign Again (2020).mkv");

        await Handler().HandleAsync(Context(31, $$$"""{"relative_media_path":"Foreign Again (2020).mkv","media_scope":"movie","library_id":{{{library}}}}"""), CancellationToken.None);

        return await _fixture.Db(uow => _fixture.Files.FindAsync(uow, library, "Foreign Again (2020).mkv")) ?? throw new InvalidOperationException("The file row is missing.");
    }

    [Fact]
    public async Task A_file_the_rules_reject_with_no_media_manager_is_recorded_as_rejected_in_plain_words()
    {
        var file = await RejectedForeignFileAsync();

        Assert.Equal(ProcessingFileStatuses.Rejected, file.Status);
        Assert.Equal($"{ForeignOnlyReason} {WeirOnlyRejection.LeftInPlace}", file.StatusReason);
        Assert.Equal((ProcessingFailureClasses.Rules, 0L, null), (file.FailureClass, file.FailureAttempts, file.NextRetryAt));
        Assert.True(File.Exists(ForeignFile));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind <> 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task A_rules_rejection_with_no_media_manager_is_titled_as_a_rejection_in_activity()
    {
        await RejectedForeignFileAsync();

        Assert.Equal(
            "Rejected Foreign Again (2020).mkv: no English audio for the \"English only\" rules",
            await ScalarText("SELECT title FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
    }

    [Fact]
    public async Task Under_the_reject_policy_the_activity_title_for_the_pass_is_unchanged()
    {
        await RejectedForeignFileAsync(failurePolicy: "reject");

        Assert.Equal(
            "Foreign Again (2020).mkv could not be checked",
            await ScalarText("SELECT title FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
    }

    private async Task<StoryStep> RecordedStoryEndAsync()
    {
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail_json FROM file_logs ORDER BY id LIMIT 1"));
        return FileStory.NarratePass(detail)[^1];
    }

    [Fact]
    public async Task A_rules_rejection_with_no_media_manager_is_told_in_the_file_timeline_as_rejected_with_what_became_of_the_file()
    {
        await RejectedForeignFileAsync();

        var step = await RecordedStoryEndAsync();

        Assert.Equal("Rejected", step.Heading);
        Assert.Equal(
            "None of its audio tracks are in English, and the \"English only\" rules keep only English audio, so there would be nothing to keep. " +
            "It has Japanese audio. To accept files like this, change the first-choice language or \"How to choose audio\" in Setup › Rules › Profiles. " +
            WeirOnlyRejection.LeftInPlace,
            step.Sentence);
    }

    [Fact]
    public async Task Under_the_reject_policy_the_file_timeline_keeps_saying_the_pass_could_not_finish()
    {
        await RejectedForeignFileAsync(failurePolicy: "reject");

        var step = await RecordedStoryEndAsync();

        Assert.Equal("Could not finish", step.Heading);
    }

    [Fact]
    public async Task A_rules_rejection_with_no_media_manager_offers_the_remove_dialog_and_deletes_only_the_file_it_recorded()
    {
        var file = await RejectedForeignFileAsync();
        Assert.NotNull(file.FingerprintSizeBytes);

        var options = await _fixture.Db(uow => Removal().EvaluateAsync(uow, file, CancellationToken.None));
        var outcome = await _fixture.Db(uow => Removal().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(options.RequiresChoice);
        Assert.False(options.DeleteHandledByManager);
        Assert.True(outcome.Done);
        Assert.False(File.Exists(ForeignFile));
    }

    [Fact]
    public async Task A_rules_rejection_with_no_media_manager_refuses_to_delete_a_file_that_changed_since()
    {
        var file = await RejectedForeignFileAsync();
        File.AppendAllText(ForeignFile, "a different release landed here");

        var outcome = await _fixture.Db(uow => Removal().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(File.Exists(ForeignFile));
    }

    [Fact]
    public async Task A_rules_rejection_stays_rejected_when_the_library_deletes_rejected_files()
    {
        var file = await RejectedForeignFileAsync(rejectedFileAction: "delete_file");

        Assert.Equal(ProcessingFileStatuses.Rejected, file.Status);
        Assert.Equal($"{ForeignOnlyReason} {WeirOnlyRejection.Deleted}", file.StatusReason);
        Assert.False(File.Exists(ForeignFile));
        Assert.Null(file.FingerprintSizeBytes);
    }

    [Fact]
    public async Task Under_the_reject_policy_a_rules_rejection_keeps_the_wording_a_media_manager_is_sent()
    {
        var file = await RejectedForeignFileAsync(failurePolicy: "reject");

        Assert.Equal(ProcessingFileStatuses.Skipped, file.Status);
        Assert.StartsWith("remux plan could not be built (no retainable audio)", file.StatusReason, StringComparison.Ordinal);
        var reject = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));
        Assert.Equal("remux plan could not be built (no retainable audio)", WireConvert.Str(reject["reason"]));
    }

    [Fact]
    public async Task A_rules_rejection_of_a_handed_off_file_keeps_the_wording_its_manager_is_sent()
    {
        var library = await LibraryAsync();
        await EnglishOnlyRulesAsync(library);
        _folders.Source("Film/film.mkv");
        _media.DefaultProbe = FakeMediaRunner.JapaneseOnly;
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h31", library, "Film/film.mkv"); return 0; });
        var handoff = $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","trigger":"webhook","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h31","callback_path":"{{{EventsPath}}}","release_name":"Film.2001"}}""";

        await Handler().HandleAsync(Context(32, handoff), CancellationToken.None);

        Assert.Equal("skipped", await ScalarText("SELECT status FROM files"));
        Assert.StartsWith("remux plan could not be built (no retainable audio)", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_execution_failure_is_retried_then_handed_back_under_pass_through_carrying_the_origin()
    {
        var library = await LibraryAsync(maxAttempts: 2);
        _folders.Source(Path.Join("Film", "film.mkv"));
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "Conversion failed";
        await FileRowAsync(library, "Film/film.mkv");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv"); return 0; });
        var handoff = $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","trigger":"webhook","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}","release_name":"Film.2001"}}""";
        var first = await EnqueueAsync(handoff, "remux:h1");

        await Handler().HandleAsync(Context(first, handoff), CancellationToken.None);

        Assert.Equal("processing_failed|1|execution", await ScalarText("SELECT status || '|' || failure_attempts || '|' || failure_class FROM files"));
        Assert.NotEqual(string.Empty, await ScalarText("SELECT coalesce(next_retry_at, '') FROM files"));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var retrying = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
        Assert.Equal("retrying", WireConvert.Str(retrying["result"]));

        // #531 item 2: the retry a scan queues has no origin of its own; the pass carries the hand-off's forward.
        var retryPayload = $$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","library_id":{{library}},"trigger":"scheduled"}""";
        var second = await EnqueueAsync(retryPayload, "remux:retry");
        await Handler().HandleAsync(Context(second, retryPayload), CancellationToken.None);

        Assert.StartsWith("processing_failed|2|", await ScalarText("SELECT status || '|' || failure_attempts || '|' FROM files"), StringComparison.Ordinal);
        var passThrough = await ScalarText("SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'");
        Assert.Equal(
            $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"trigger":"worker","origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"{{{EventsPath}}}","release_name":"Film.2001"}}""",
            passThrough);
        // #545 item 2: the source's fingerprint is folded into the dedupe key so a later failure of a replaced file
        // queues again; the base key (kind, library, path) is still a prefix of it.
        Assert.StartsWith(
            $"processing.file.pass_through.v1:{library}:Film/film.mkv:",
            await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"),
            StringComparison.Ordinal);
        Assert.Contains("handing the original back to the output folder unchanged", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
        // The pass-through reports on its own once delivered, so nothing is sent yet.
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task A_replaced_file_queues_again_after_the_first_pass_through_finished()
    {
        // A pass-through job dedupe-keyed by path alone would block forever — once one finishes, a later failure
        // of a re-download with the same name would never queue another. The fingerprint folded into the key
        // (size + modification time) tells the two files apart, while a repeat enqueue for the very same,
        // unchanged file still dedupes against the row already sitting there.
        var library = await LibraryAsync(maxAttempts: 1);
        var path = _folders.Source(Path.Join("Film", "film.mkv"), 10);
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "Conversion failed";
        await FileRowAsync(library, "Film/film.mkv");
        var payload = $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","library_id":{{{library}}}}""";

        var first = await EnqueueAsync(payload, "remux:1");
        await Handler().HandleAsync(Context(first, payload), CancellationToken.None);

        var firstKey = await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'");
        Assert.StartsWith($"processing.file.pass_through.v1:{library}:Film/film.mkv:", firstKey, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));

        // Re-enqueuing a failure for the very same, unchanged file stays idempotent against the still-pending row.
        var again = await EnqueueAsync(payload, "remux:1b");
        await Handler().HandleAsync(Context(again, payload), CancellationToken.None);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));

        // The manager delivers, then a re-download replaces the file at the same path with different content.
        await _fixture.Store.Execute("UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.pass_through.v1'");
        File.WriteAllBytes(path, new byte[20]);

        var second = await EnqueueAsync(payload, "remux:2");
        await Handler().HandleAsync(Context(second, payload), CancellationToken.None);

        Assert.Equal(2, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
        var secondKey = await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.pass_through.v1' AND status = 'pending'");
        Assert.NotEqual(firstKey, secondKey);
        Assert.StartsWith($"processing.file.pass_through.v1:{library}:Film/film.mkv:", secondKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retried_hand_off_that_succeeds_is_reported_with_its_output_path()
    {
        var library = await LibraryAsync();
        _folders.Source(Path.Join("Film", "film.mkv"));
        await FileRowAsync(library, "Film/film.mkv", "processing_failed");
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h2", library, "Film/film.mkv"); return 0; });
        await EnqueueAsync($$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h2","callback_path":"{{{EventsPath}}}"}}""", "remux:h2");
        var retryPayload = $$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","library_id":{{library}},"trigger":"scheduled"}""";
        var retry = await EnqueueAsync(retryPayload, "remux:retry2");

        await Handler().HandleAsync(Context(retry, retryPayload), CancellationToken.None);

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (WireObject)post.Json!;
        Assert.Equal(("h2", "completed"), (WireConvert.Str(body["handoffId"]), WireConvert.Str(body["status"])));
        Assert.Equal(Path.GetFullPath(_folders.Out(Path.Join("Film", "film.mkv"))), WireConvert.Str(body["outputPath"]));
        Assert.Equal("completed", await ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h2'"));
        Assert.Equal(Path.GetFullPath(_folders.Out(Path.Join("Film", "film.mkv"))), await ScalarText("SELECT output_path FROM media_manager_handoffs WHERE handoff_id = 'h2'"));
    }

    [Fact]
    public async Task An_origin_is_never_carried_to_a_hand_off_that_was_already_answered()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h3", library, "Film/film.mkv"); return 0; });
        await EnqueueAsync($$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h3"}}""", "remux:h3");

        Assert.NotNull(await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, library, "Film/film.mkv")));
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h3", "completed", "/out/film.mkv"); return 0; });
        Assert.Null(await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, library, "Film/film.mkv")));
        Assert.Null(await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, library, "Other/film.mkv")));
    }

    [Fact]
    public async Task Unreadable_content_is_not_rejected_on_the_first_look()
    {
        var library = await LibraryAsync(failurePolicy: "reject");
        _folders.Source("bad.mkv");
        _media.ProbeError = "[matroska,webm @ 0x1] EBML header parsing failed\nbad.mkv: Invalid data found when processing input";
        await FileRowAsync(library, "bad.mkv");

        await Handler().HandleAsync(Context(8, $$$"""{"relative_media_path":"bad.mkv","library_id":{{{library}}}}"""), CancellationToken.None);

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1' AND status = 'pending'"));
    }

    [Fact]
    public async Task Unreadable_content_that_stayed_unreadable_under_reject_queues_a_reject_instead_of_a_hand_back()
    {
        var library = await LibraryAsync(failurePolicy: "reject");
        var source = _folders.Source("bad.mkv");
        _media.ProbeError = "[matroska,webm @ 0x1] EBML header parsing failed\nbad.mkv: Invalid data found when processing input";
        await FileRowAsync(library, "bad.mkv");
        var info = new FileInfo(source);
        var fingerprint = $"{info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var looks = RemuxPassHandler.UnreadableWaitMinutes.Count;

        await Handler().HandleAsync(
            Context(9, $$$"""{"relative_media_path":"bad.mkv","library_id":{{{library}}},"unreadable_looks":{{{looks}}},"unreadable_fingerprint":"{{{fingerprint}}}"}"""),
            CancellationToken.None);

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"));
        var reject = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.reject.v1'"));
        Assert.Equal("preflight", WireConvert.Str(reject["failure_class"]));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events"));
        Assert.Equal((true, "failed"), (((WireBool)detail["reject_queued"]).Value, WireConvert.Str(detail["result"])));
        Assert.False(detail.ContainsKey("next_action"));
    }

    [Fact]
    public async Task A_workflow_that_allows_five_attempts_retries_four_times_and_gives_up_on_the_fifth_failure()
    {
        var library = await LibraryAsync(failurePolicy: "hold", maxAttempts: 5);
        _folders.Source("one.mkv");
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "boom";
        await FileRowAsync(library, "one.mkv");
        var payload = $$$"""{"relative_media_path":"one.mkv","library_id":{{{library}}}}""";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Handler().HandleAsync(Context(100 + attempt, payload), CancellationToken.None);

            Assert.Equal($"processing_failed|{attempt}|retry owed", await RetryStateAsync());
        }

        await Handler().HandleAsync(Context(105, payload), CancellationToken.None);

        Assert.Equal("processing_failed|5|no retry", await RetryStateAsync());
        Assert.Contains("tried this file 5 times and stopped, because the Movies workflow allows 5", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_attempt_books_a_look_at_the_folder_for_when_its_retry_falls_due()
    {
        var wakeups = new ScanWakeups();
        var library = await LibraryAsync(maxAttempts: 3);
        _folders.Source("one.mkv");
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "boom";
        await FileRowAsync(library, "one.mkv");

        await Handler(wakeups: wakeups).HandleAsync(Context(11, $$$"""{"relative_media_path":"one.mkv","library_id":{{{library}}}}"""), CancellationToken.None);

        var retryAt = TimestampColumns.Parse(await ScalarText("SELECT next_retry_at FROM files"));
        var booked = wakeups.BookedFor(library);
        Assert.NotNull(retryAt);
        Assert.NotNull(booked);
        Assert.InRange((booked.Value - retryAt.Value).TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public async Task A_failure_with_no_retry_owed_books_no_look()
    {
        var wakeups = new ScanWakeups();
        var library = await LibraryAsync(maxAttempts: 1);
        _folders.Source("one.mkv");
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "boom";
        await FileRowAsync(library, "one.mkv");

        await Handler(wakeups: wakeups).HandleAsync(Context(11, $$$"""{"relative_media_path":"one.mkv","library_id":{{{library}}}}"""), CancellationToken.None);

        Assert.Null(wakeups.BookedFor(library));
    }

    private Task<string> RetryStateAsync() =>
        ScalarText("SELECT status || '|' || failure_attempts || '|' || CASE WHEN next_retry_at IS NULL THEN 'no retry' ELSE 'retry owed' END FROM files");

    [Fact]
    public async Task The_hold_policy_queues_no_follow_up_and_the_holding_seam_never_does()
    {
        var library = await LibraryAsync(failurePolicy: "hold", maxAttempts: 1);
        _folders.Source("one.mkv");
        _media.DefaultProbe = FakeMediaRunner.EnglishAndJapanese;
        _media.RemuxError = "boom";
        await FileRowAsync(library, "one.mkv");

        await Handler().HandleAsync(Context(11, $$$"""{"relative_media_path":"one.mkv","library_id":{{{library}}}}"""), CancellationToken.None);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));

        await LibraryAsync(failurePolicy: "pass_through", maxAttempts: 1);
        await FileRowAsync(await _fixture.Store.Scalar("SELECT id FROM libraries"), "one.mkv");
        await Handler(new HoldingFailurePolicy()).HandleAsync(Context(12, """{"relative_media_path":"one.mkv"}"""), CancellationToken.None);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
    }

    [Theory]
    [InlineData("", "missing payload_json")]
    [InlineData("{nope", "invalid json: ")]
    [InlineData("[1]", "payload must be a JSON object")]
    [InlineData("""{"media_scope":"movie"}""", "relative_media_path is required")]
    [InlineData("""{"relative_media_path":"a.mkv","dry_run":false}""", "legacy Weir dry_run")]
    public async Task A_payload_that_cannot_run_is_recorded_as_a_failed_check(string payload, string reason)
    {
        await LibraryAsync();

        await Handler().HandleAsync(Context(7, payload), CancellationToken.None);

        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events"));
        Assert.Equal("failed_before_execution", WireConvert.Str(detail["outcome"]));
        Assert.Contains(reason, WireConvert.Str(detail["reason"]), StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task A_library_whose_folders_cannot_be_used_fails_before_execution_with_its_reason()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute("UPDATE libraries SET output_folder = ''");
        await FileRowAsync(library, "a.mkv");

        await Handler().HandleAsync(Context(8, $$"""{"relative_media_path":"a.mkv","library_id":{{library}},"trigger":"manual"}"""), CancellationToken.None);

        var detail = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT detail FROM activity_events"));
        Assert.Contains("has no output folder set", WireConvert.Str(detail["reason"]), StringComparison.Ordinal);
        Assert.Equal("manual", WireConvert.Str(detail["trigger"]));
        Assert.Equal("processing_failed|preflight", await ScalarText("SELECT status || '|' || failure_class FROM files"));
    }

    [Fact]
    public async Task A_handler_crash_is_recorded_against_the_file_with_the_policy_applied()
    {
        var library = await LibraryAsync(maxAttempts: 1);
        await FileRowAsync(library, "crash.mkv");
        var recorder = new RemuxPassFailureRecorder(_fixture.Store.Database, new QueueingFailurePolicy(_fixture.Jobs), _fixture.Libraries, TimeProvider.System, NullLogger<RemuxPassFailureRecorder>.Instance);
        var payload = $$$"""{"relative_media_path":"crash.mkv","library_id":{{{library}}},"origin":{"source_key":"deluno","handoff_id":"h9"}}""";

        var willRetry = await recorder.RecordAsync(new UnhandledJobFailure(Context(1, payload), library, "movie", "crash.mkv", "boom"), CancellationToken.None);

        Assert.False(willRetry);
        Assert.Equal("processing_failed|unknown", await ScalarText("SELECT status || '|' || failure_class FROM files"));
        Assert.Contains("\"handoff_id\":\"h9\"", await ScalarText("SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.pass_through.v1'"), StringComparison.Ordinal);
        Assert.Null(await recorder.RecordAsync(new UnhandledJobFailure(Context(1, "{}"), null, "tv", null, "boom"), CancellationToken.None));
    }

    [Fact]
    public async Task The_worker_claims_remux_pass_jobs_once_the_handler_is_registered()
    {
        var registry = new JobHandlerRegistry([Handler()]);
        Assert.Equal([RemuxPassOutcomes.JobKind], registry.JobKinds);
        Assert.Contains(RemuxPassOutcomes.JobKind, ClaimableKinds.For(registry).HandledKinds);
        await Task.CompletedTask;
    }
}
