using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What a pass does when the bookkeeping around it cannot be saved or read: the media work is never undone for it.</summary>
public sealed partial class RemuxPassHandlerTests
{
    private const string UnreadableText = "[matroska,webm @ 000001b280590a00] EBML header parsing failed\nC:\\Weir\\Ready\\Movies\\file.mkv: Invalid data found when processing input";

    private static readonly string TooDeep = new string('[', 200) + new string(']', 200);

    private async Task AssertProcessedAsync()
    {
        Assert.True(await ScalarText("SELECT status FROM files") == ProcessingFileStatuses.Processed, await ScalarText("SELECT status_reason FROM files"));
        Assert.True(File.Exists(_folders.Out("Movie/file.mkv")));
    }

    [Fact]
    public async Task A_pass_whose_durable_file_state_cannot_be_saved_still_writes_and_records_its_output()
    {
        var library = await LibraryWithOneFileAsync();
        await _fixture.Store.Execute("CREATE TRIGGER refuse_done BEFORE UPDATE ON files WHEN NEW.status = 'processed' BEGIN SELECT RAISE(ABORT, 'files refused'); END");

        await Handler().HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        Assert.True(File.Exists(_folders.Out("Movie/file.mkv")));
        Assert.Equal(ProcessingFileStatuses.Processing, await ScalarText("SELECT status FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
    }

    [Fact]
    public async Task A_pass_whose_processing_record_cannot_be_saved_still_finishes_the_file()
    {
        var library = await LibraryWithOneFileAsync();
        await _fixture.Store.Execute("CREATE TRIGGER refuse_record BEFORE UPDATE ON activity_events WHEN NEW.event_type = 'processing.file_remux_pass_completed' BEGIN SELECT RAISE(ABORT, 'record refused'); END");

        await Handler().HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        await AssertProcessedAsync();
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
    }

    [Fact]
    public async Task A_pass_with_no_manager_reporter_finishes_without_telling_anyone()
    {
        var library = await LibraryWithOneFileAsync();

        await Handler(reportsBack: false).HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        await AssertProcessedAsync();
        Assert.Empty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_hand_off_report_that_blows_up_does_not_undo_the_finished_pass()
    {
        var library = await LibraryWithOneFileAsync();
        var origin = "\"origin\":{\"source_key\":\"deluno\",\"handoff_id\":\"h1\",\"library_id\":\"lib\",\"callback_path\":\"" + EventsPath + "\"}";
        var payload = $$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{library}},{{origin}}}""";

        await Handler(beforeReport: () => _fixture.Store.Execute("PRAGMA foreign_keys=OFF; DROP TABLE media_manager_connections")).HandleAsync(Context(1, payload), CancellationToken.None);

        await AssertProcessedAsync();
    }

    [Fact]
    public async Task A_second_job_row_with_unreadable_json_does_not_stop_the_pass_finding_its_own_origin()
    {
        var library = await LibraryWithOneFileAsync();
        var jobId = await EnqueueAsync("{\"origin\":" + TooDeep + "}", "too-deep");

        await Handler().HandleAsync(Context(jobId, PassPayload(library)), CancellationToken.None);

        await AssertProcessedAsync();
    }

    [Fact]
    public async Task A_pass_that_passes_the_file_through_unchanged_is_never_taken_for_a_repeat()
    {
        var library = await LibraryWithOneFileAsync();
        _media.Probes["file.mkv"] = FakeMediaRunner.EnglishOnly;
        var payload = $$"""{"relative_media_path":"Movie/file.mkv","media_scope":"movie","library_id":{{library}},"pass_through_unchanged":true}""";

        await Handler().HandleAsync(Context(1, payload), CancellationToken.None);

        await AssertProcessedAsync();
    }

    [Fact]
    public async Task An_outcome_with_no_file_path_changes_no_file_row()
    {
        var library = await LibraryWithOneFileAsync();

        await Handler().ApplyFileOutcomeStateAsync(new WireObject().Set("ok", true), library, "movie", null);

        Assert.Equal(ProcessingFileStatuses.Unprocessed, await ScalarText("SELECT status FROM files"));
    }

    [Fact]
    public async Task An_unreadable_file_is_not_booked_for_another_look_when_nothing_can_queue_one()
    {
        var library = await LibraryWithOneFileAsync();
        _media.ProbeError = UnreadableText;

        await Handler(queuesLooks: false).HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        Assert.Equal(ProcessingFileStatuses.OnHold, await ScalarText("SELECT status FROM files"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'"));
    }

    [Fact]
    public async Task A_file_too_new_to_touch_is_not_booked_for_another_look_when_nothing_can_queue_one()
    {
        var library = await LibraryAsync(readyAfterSeconds: 3600);
        _folders.Source(Path.Join("Movie", "file.mkv"));
        await FileRowAsync(library, "Movie/file.mkv", ProcessingFileStatuses.Unprocessed);

        await Handler(queuesLooks: false).HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        Assert.Equal(ProcessingFileStatuses.OnHold, await ScalarText("SELECT status FROM files"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'"));
    }

    [Fact]
    public async Task A_look_the_queue_refuses_leaves_the_file_on_hold_with_its_reason()
    {
        var library = await LibraryWithOneFileAsync();
        _media.ProbeError = UnreadableText;
        await _fixture.Store.Execute("CREATE TRIGGER refuse_look BEFORE INSERT ON jobs WHEN NEW.dedupe_key LIKE '%unreadable-wait%' BEGIN SELECT RAISE(ABORT, 'queue refused'); END");

        await Handler().HandleAsync(Context(1, PassPayload(library)), CancellationToken.None);

        Assert.Equal(ProcessingFileStatuses.OnHold, await ScalarText("SELECT status FROM files"));
        Assert.Contains("look again", await ScalarText("SELECT status_reason FROM files"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE dedupe_key LIKE '%unreadable-wait%'"));
    }

    [Fact]
    public async Task A_job_that_is_not_this_files_live_hand_off_carries_no_origin()
    {
        var library = await LibraryWithOneFileAsync();
        const string Origin = "\"origin\":{\"source_key\":\"deluno\",\"handoff_id\":\"h9\"}";
        await EnqueueAsync("""{"relative_media_path":"Movie/file.mkv","origin":{"source_key":"deluno","handoff_id":"unknown"}}""", "not-in-ledger");
        await EnqueueAsync("{\"relative_media_path\":\"Movie/file.mkv\",\"origin\":" + TooDeep + "}", "too-deep");
        await EnqueueAsync("{\"relative_media_path\":\"Movie/other.mkv\"," + Origin + "}", "other-file");
        await EnqueueAsync($$"""{"relative_media_path":"Movie/file.mkv","library_id":{{library + 1}},{{Origin}}}""", "other-workflow");
        await EnqueueAsync("""{"relative_media_path":"Movie/file.mkv","origin":{"x":1}}""", "no-hand-off");

        var carried = await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, library, "Movie/file.mkv"));

        Assert.Null(carried);
    }
}
