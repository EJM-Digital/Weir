using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What the pass-through and reject follow-ups, the failure policy and the progress row do with payloads and databases that are not as expected.</summary>
public sealed class FollowUpAndProgressEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private ProcessingPassThroughHandler PassThrough(bool withReporter = true) =>
        new(_fixture.Store.Database, TimeProvider.System, NullLogger<ProcessingPassThroughHandler>.Instance, _fixture.Handback, _fixture.Libraries, _fixture.Jobs, withReporter ? _fixture.Reporter : null)
        {
            GoneSettle = TimeSpan.Zero,
        };

    private ProcessingRejectHandler Reject() =>
        new(
            _fixture.Store.Database, _fixture.Ports, _fixture.Connections, _fixture.Reporter, new RejectRoutes(_fixture.Ports, _fixture.Reporter), _fixture.Ledger,
            _fixture.Jobs, new RejectPacing(TimeProvider.System), _fixture.Libraries, TimeProvider.System, NullLogger<ProcessingRejectHandler>.Instance);

    private async Task<long> LibraryAsync(string policy = "pass_through", string? output = null)
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, $policy, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", output ?? _folders.Output),
            ("$k", _folders.Work),
            ("$policy", policy))), CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("{not json")]
    public void A_payload_that_is_missing_or_not_an_object_reads_as_empty(string? payload)
    {
        var parsed = FollowUpJobPayload.Parse(payload);

        Assert.Empty(parsed.Items);
        Assert.Equal(string.Empty, FollowUpJobPayload.RelativeMediaPath(parsed));
        Assert.Null(FollowUpJobPayload.LibraryId(parsed));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"relative_media_path":"a.mkv"}""")]
    [InlineData("""{"library_id":1}""")]
    public async Task A_follow_up_without_a_file_and_a_workflow_is_refused(string payload)
    {
        var context = new JobWorkContext(1, IntakeRules.PassThroughJobKind, payload, "test");

        var passThrough = await Assert.ThrowsAsync<InvalidOperationException>(() => PassThrough().HandleAsync(context, CancellationToken.None));
        var reject = await Assert.ThrowsAsync<InvalidOperationException>(() => Reject().HandleAsync(context, CancellationToken.None));

        Assert.Equal("A pass-through job needs a file and a workflow.", passThrough.Message);
        Assert.Equal("A reject job needs a file and a workflow.", reject.Message);
    }

    [Fact]
    public async Task A_vanished_file_with_no_reporter_to_tell_is_still_forgotten()
    {
        var library = await LibraryAsync();
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({library}, 'gone.mkv', 'processing_failed', 'failed')");
        var origin = "\"origin\":{\"source_key\":\"deluno\",\"handoff_id\":\"h\",\"callback_path\":\"/api/integrations/processors/events\"}";
        var payload = $$"""{"relative_media_path":"gone.mkv","library_id":{{library}},"gone_looks":1,{{origin}}}""";

        await PassThrough(withReporter: false).HandleAsync(new JobWorkContext(2, IntakeRules.PassThroughJobKind, payload, "test"), CancellationToken.None);

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Empty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_failed_hand_back_keeps_the_technical_detail_when_the_plain_words_differ_from_the_error()
    {
        Directory.CreateDirectory(_folders.Out("file.mkv"));
        var library = await LibraryAsync();
        _folders.Source("file.mkv");
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({library}, 'file.mkv', 'processing_failed', 'failed')");
        var payload = $$"""{"relative_media_path":"file.mkv","library_id":{{library}}}""";

        await Assert.ThrowsAsync<AlreadyRecordedFailureException>(() => PassThrough().HandleAsync(new JobWorkContext(3, IntakeRules.PassThroughJobKind, payload, "test"), CancellationToken.None));

        var detail = (WireObject)WireJsonParser.Parse((await _fixture.Store.ScalarText("SELECT detail FROM activity_events WHERE result = 'failed'"))!);
        Assert.Equal(ToolFailureText.OutputNotPlaced, WireConvert.Str(detail["message"]));
        Assert.True(detail.ContainsKey("technical_detail"));
    }

    [Fact]
    public async Task A_bad_release_under_the_reject_policy_is_handed_back_to_the_manager_and_the_file_says_so()
    {
        var library = await LibraryAsync(policy: "reject");
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({library}, 'bad.mkv', 'processing_failed', 'ffmpeg could not read it')");
        var record = (await _fixture.Db(uow => _fixture.Libraries.GetAsync(uow, library)))!;

        var applied = await _fixture.Db(uow => new QueueingFailurePolicy(_fixture.Jobs).ApplyFailurePolicyAsync(uow, record, "bad.mkv", false, null, badRelease: true));

        Assert.Equal(ProcessingFailurePolicies.Reject, applied);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.RejectJobKind}'"));
        Assert.Contains("telling your media manager this release is bad", (await _fixture.Store.ScalarText("SELECT status_reason FROM files"))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_release_with_no_reason_on_record_is_handed_back_with_a_plain_one()
    {
        var library = await LibraryAsync(policy: "reject");
        var record = (await _fixture.Db(uow => _fixture.Libraries.GetAsync(uow, library)))!;

        var applied = await _fixture.Db(uow => new QueueingFailurePolicy(_fixture.Jobs).ApplyFailurePolicyAsync(uow, record, "bad.mkv", false, null, badRelease: true));

        Assert.Equal(ProcessingFailurePolicies.Reject, applied);
        Assert.Contains("Weir could not read this file's contents.", (await _fixture.Store.ScalarText($"SELECT payload_json FROM jobs WHERE job_kind = '{IntakeRules.RejectJobKind}'"))!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_path_that_cannot_be_read_is_tagged_unreadable_in_the_dedupe_key()
    {
        var library = new ProcessingLibraryRecord { Name = "Movies", WatchedFolder = _folders.Watched };

        Assert.Equal("unreadable", QueueingFailurePolicy.FingerprintTag(library, "bad\0name.mkv"));
    }

    [Fact]
    public async Task The_holding_policy_queues_nothing_and_rejects_nothing()
    {
        var policy = new HoldingFailurePolicy();
        var library = new ProcessingLibraryRecord { Name = "Movies" };

        await policy.QueueHandoffRetryAsync(null!, library, "a.mkv", new WireObject(), DateTimeOffset.UnixEpoch);

        Assert.False(await policy.RejectBadReleaseAsync(null!, library, "a.mkv", "reason", null, null));
        Assert.Null(await policy.ApplyFailurePolicyAsync(null!, library, "a.mkv", false, null, true));
    }

    [Fact]
    public void A_path_that_does_not_exist_and_has_no_parent_stays_where_it_is()
    {
        Assert.Equal(string.Empty, FileLifecycle.NearestExistingParent("no-such-folder-here"));
        Assert.Equal(string.Empty, FileLifecycle.NearestExistingParent(string.Empty));
    }

    [Fact]
    public async Task The_progress_row_is_retitled_for_each_stage_and_flushing_waits_for_the_save()
    {
        var reporter = new ActivityProgressReporter(_fixture.Store.Database, 5, new WireObject().Set("relative_media_path", "Film/Film.mkv"), NullLogger.Instance, TimeProvider.System, new LiveProgressStore());

        foreach (var status in new[] { "processing", "waiting", "processing" })
        {
            reporter.Report(new WireObject().Set("status", status));
            await reporter.FlushAsync();
        }

        Assert.Equal("Processing Film.mkv", await _fixture.Store.ScalarText("SELECT title FROM activity_events"));
        reporter.Report(new WireObject().Set("status", "waiting"));
        await reporter.FlushAsync();
        Assert.Equal("Waiting to process Film.mkv", await _fixture.Store.ScalarText("SELECT title FROM activity_events"));
    }

    [Fact]
    public async Task A_progress_save_the_database_refuses_is_logged_and_the_pass_carries_on()
    {
        await _fixture.Store.Execute("CREATE TRIGGER refuse_progress BEFORE INSERT ON activity_events BEGIN SELECT RAISE(ABORT, 'progress refused'); END");
        var logger = new CapturingLogger<ActivityProgressReporter>();
        var reporter = new ActivityProgressReporter(_fixture.Store.Database, 6, new WireObject().Set("relative_media_path", "Film/Film.mkv"), logger, TimeProvider.System, new LiveProgressStore());

        reporter.Report(new WireObject().Set("status", "processing"));
        await reporter.FlushAsync();

        Assert.True(logger.Logged(LogLevel.Warning, "could not save a progress update"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public void A_progress_block_says_how_much_was_read_and_written_so_far_and_nothing_when_the_length_is_unknown()
    {
        var known = RemuxPassRunner.ProgressWithUpdate(
            new WireObject(), new FfmpegProgressUpdate { Percent = 25.0, ElapsedSeconds = 4, ProcessedSeconds = 10.5, Progress = "continue", TotalSizeBytes = 180 }, 1000);
        var unknown = RemuxPassRunner.ProgressWithUpdate(new WireObject(), new FfmpegProgressUpdate { Progress = "continue" }, 1000);
        var withoutWritten = RemuxPassRunner.ProgressWithUpdate(new WireObject(), new FfmpegProgressUpdate { Percent = 50.0, Progress = "continue" }, 1000);

        Assert.Equal((250L, 180L, 25.0), (((WireInteger)known["bytes_read"]).Value, ((WireInteger)known["bytes_written"]).Value, ((WireNumber)known["percent"]).Value));
        Assert.Equal((WireNull.Instance, WireNull.Instance, WireNull.Instance), (unknown["percent"], unknown["bytes_read"], unknown["processed_seconds"]));
        Assert.Equal(500L, ((WireInteger)withoutWritten["bytes_written"]).Value);
    }
}
