using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// One worker step: success, failure and retry reporting, retired job kinds, finalize failures, and the
/// rule that a kind with no registered handler is never claimed.
/// </summary>
public sealed class ProcessingJobProcessorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;
    private readonly JobsTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Process_one_is_idle_when_no_jobs()
    {
        Assert.Equal(JobProcessOutcome.Idle, await _db.Processor().ProcessOneAsync("test-owner"));
    }

    [Fact]
    public async Task Process_one_completes_with_success_handler()
    {
        await _db.Store.EnqueueOrGetAsync("d1", "processing.test.ok.v1", maxAttempts: 3);
        JobWorkContext? seen = null;
        var notifications = new RecordingNotifications();
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", context => seen = context)], notifications: notifications);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Equal(new JobWorkContext(1, "processing.test.ok.v1", null, "w", 1, 3), seen);
        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
        Assert.Equal(["completed 1 processing.test.ok.v1 willRetry=False"], notifications.Sent);
    }

    [Theory]
    [InlineData(JobAlert.Finished, "completed 1 processing.test.ok.v1 willRetry=False")]
    [InlineData(JobAlert.FailedForGood, "failed-for-good 1 processing.test.ok.v1")]
    public async Task A_handler_says_what_the_alert_for_its_finished_job_is(JobAlert alert, string sent)
    {
        await _db.Store.EnqueueOrGetAsync("d1", "processing.test.ok.v1", maxAttempts: 3);
        var notifications = new RecordingNotifications();
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", context => context.Alert = alert)], notifications: notifications);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
        Assert.Equal([sent], notifications.Sent);
    }

    [Fact]
    public async Task A_handler_that_says_nothing_happened_sends_no_alert_at_all()
    {
        await _db.Store.EnqueueOrGetAsync("d1", "processing.test.ok.v1", maxAttempts: 3);
        var notifications = new RecordingNotifications();
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", context => context.Alert = JobAlert.Nothing)], notifications: notifications);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
        Assert.Empty(notifications.Sent);
    }

    [Fact]
    public async Task Process_one_fail_path_on_handler_error()
    {
        await _db.Store.EnqueueOrGetAsync("d2", "processing.test.bad.v1", maxAttempts: 1);
        var notifications = new RecordingNotifications();
        var processor = _db.Processor([new DelegateHandler("processing.test.bad.v1", _ => throw new InvalidOperationException("boom"))], notifications: notifications);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Failed, row.Status);
        Assert.Contains("Weir job failed", row.LastError, StringComparison.Ordinal);
        Assert.Contains("marked failed", row.LastError, StringComparison.Ordinal);
        Assert.Equal(["failed 1 processing.test.bad.v1 willRetry=False"], notifications.Sent);
    }

    private static async Task<string> NextTopicAsync(BroadcastSubscription<string> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var topic in subscription.ReadAllAsync(timeout.Token))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    [Fact]
    public async Task A_library_scan_that_completes_says_so_on_library_scan_once_its_row_is_final()
    {
        var changes = new DataChangePublisher();
        using var subscription = changes.Subscribe();
        await _db.Store.EnqueueOrGetAsync("scan", LibraryModeJobKinds.ScanKind, "{\"library_id\":1}");
        string? statusWhenHandled = null;
        var processor = _db.Processor(
            [new DelegateHandler(LibraryModeJobKinds.ScanKind, _ => statusWhenHandled = _db.Store.GetAsync(1).GetAwaiter().GetResult()!.Status)],
            changes: changes);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Equal(DataTopics.LibraryScan, await NextTopicAsync(subscription));
        Assert.Equal((ProcessingJobStatus.Leased, ProcessingJobStatus.Completed), (statusWhenHandled, (await _db.Store.GetAsync(1))!.Status));
    }

    [Fact]
    public async Task A_library_scan_that_fails_says_so_on_library_scan_too()
    {
        var changes = new DataChangePublisher();
        using var subscription = changes.Subscribe();
        await _db.Store.EnqueueOrGetAsync("scan", LibraryModeJobKinds.ScanKind, "{\"library_id\":1}", maxAttempts: 1);
        var processor = _db.Processor([new DelegateHandler(LibraryModeJobKinds.ScanKind, _ => throw new InvalidOperationException("boom"))], changes: changes);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Equal(DataTopics.LibraryScan, await NextTopicAsync(subscription));
        Assert.Equal(ProcessingJobStatus.Failed, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task A_job_that_is_not_a_library_scan_says_nothing_on_library_scan()
    {
        var changes = new DataChangePublisher();
        using var subscription = changes.Subscribe();
        await _db.Store.EnqueueOrGetAsync("d1", "processing.test.ok.v1", maxAttempts: 3);
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", _ => { })], changes: changes);

        await processor.ProcessOneAsync("w", 3600, T0);
        changes.Publish(DataTopics.Settings);

        Assert.Equal(DataTopics.Settings, await NextTopicAsync(subscription));
    }

    [Fact]
    public async Task A_cleanup_job_that_runs_is_reported_to_the_task_list_as_finished_and_working()
    {
        var tasks = new PeriodicTaskRegistry(_db.Clock);
        tasks.Plan(ScheduledJobTasks.LeftoverFiles.Key, ScheduledJobTasks.LeftoverFiles.Label, T0.AddHours(1), TimeSpan.FromHours(1));
        await _db.Store.EnqueueOrGetAsync("sweep", PeriodicJobKinds.WorkTempStaleSweep, "{\"media_scope\":\"movie\"}");
        var running = false;
        var processor = _db.Processor([new DelegateHandler(PeriodicJobKinds.WorkTempStaleSweep, _ => running = tasks.Snapshot().Single().Running)], tasks: tasks);

        await processor.ProcessOneAsync("w", 3600, T0);

        var task = Assert.Single(tasks.Snapshot());
        Assert.Equal((true, false, true, null), (running, task.Running, task.LastOk, task.LastError));
    }

    [Fact]
    public async Task A_cleanup_job_that_fails_is_reported_to_the_task_list_with_a_plain_reason()
    {
        var tasks = new PeriodicTaskRegistry(_db.Clock);
        tasks.Plan(ScheduledJobTasks.LeftoverFiles.Key, ScheduledJobTasks.LeftoverFiles.Label, T0.AddHours(1), TimeSpan.FromHours(1));
        await _db.Store.EnqueueOrGetAsync("sweep", PeriodicJobKinds.WorkTempStaleSweep, "{\"media_scope\":\"movie\"}", maxAttempts: 1);
        var processor = _db.Processor([new DelegateHandler(PeriodicJobKinds.WorkTempStaleSweep, _ => throw new InvalidOperationException("boom"))], tasks: tasks);

        await processor.ProcessOneAsync("w", 3600, T0);

        var task = Assert.Single(tasks.Snapshot());
        Assert.Equal((false, "The job hit an unexpected error."), (task.LastOk, task.LastError));
    }

    [Fact]
    public async Task A_job_no_timer_is_responsible_for_adds_nothing_to_the_task_list()
    {
        var tasks = new PeriodicTaskRegistry(_db.Clock);
        await _db.Store.EnqueueOrGetAsync("d1", "processing.test.ok.v1");
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", _ => { })], tasks: tasks);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Empty(tasks.Snapshot());
    }

    [Fact]
    public async Task Process_one_marks_a_failure_notification_retry_aware_when_another_attempt_follows()
    {
        // #540 item 6: the notification wording says a retry is coming instead of always claiming
        // retries are exhausted.
        await _db.Store.EnqueueOrGetAsync("d2b", "processing.test.bad.v1", maxAttempts: 3);
        var notifications = new RecordingNotifications();
        var processor = _db.Processor([new DelegateHandler("processing.test.bad.v1", _ => throw new InvalidOperationException("boom"))], notifications: notifications);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Equal(ProcessingJobStatus.Pending, (await _db.Store.GetAsync(1))!.Status);
        Assert.Equal(["failed 1 processing.test.bad.v1 willRetry=True"], notifications.Sent);
    }

    [Fact]
    public async Task A_failure_with_attempts_left_says_it_will_be_tried_again()
    {
        await _db.Store.EnqueueOrGetAsync("a", "processing.test.bad.v1", maxAttempts: 3);
        var processor = _db.Processor([new DelegateHandler("processing.test.bad.v1", _ => throw new InvalidOperationException("boom"))]);

        await processor.ProcessOneAsync("w", now: T0);

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, row.Status);
        Assert.Contains("try this job again shortly", row.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("marked failed", row.LastError, StringComparison.Ordinal);
        Assert.Equal(
            "Weir job failed: The job hit an unexpected error. Weir will try this job again shortly. Technical detail: InvalidOperationException: boom",
            row.LastError);
    }

    [Fact]
    public async Task A_handler_failure_is_logged_as_a_sentence_with_the_tools_text_only_in_the_exception()
    {
        await _db.Store.EnqueueOrGetAsync("u", "processing.test.unreadable.v1", maxAttempts: 1);
        const string toolText = "[matroska,webm @ 000001534eaabd00] EBML header parsing failed";
        var logger = new ListLogger<ProcessingJobProcessor>();
        var processor = _db.Processor([new DelegateHandler("processing.test.unreadable.v1", _ => throw new MediaUnreadableException(toolText))], logger: logger);

        await processor.ProcessOneAsync("w", now: T0);

        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith(
            $"Job handler failed for job_id=1 kind=processing.test.unreadable.v1: Weir job failed: {ToolFailureText.UnreadableFile}",
            line.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected error", line.Message, StringComparison.Ordinal);
        Assert.Contains(ToolFailureText.UnreadableFile, (await _db.Store.GetAsync(1))!.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("matroska", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@ 0", line.Message, StringComparison.Ordinal);
        Assert.Equal(toolText, Assert.Single(logger.Exceptions).Message);
    }

    [Fact]
    public async Task An_unrecorded_handler_failure_writes_one_activity_entry_with_the_documented_detail()
    {
        await _db.Store.EnqueueOrGetAsync(
            "c",
            "processing.test.crash.v1",
            "{\"relative_media_path\": \" Films/Café.mkv \", \"library_id\": 4, \"media_scope\": \"tv\", \"trigger\": \"Webhook\", \"run_id\": \"r-1\"}",
            maxAttempts: 1);
        var recorder = new RecordingFailureRecorder(willRetry: null);
        var processor = _db.Processor([new DelegateHandler("processing.test.crash.v1", _ => throw new InvalidOperationException("line one\n   line two"))], recorder);

        await processor.ProcessOneAsync("w", now: T0);

        var entry = Assert.Single(_db.ActivityEvents());
        Assert.Equal(ActivityEventTypes.ProcessingWorkerFailure, entry.EventType);
        Assert.Equal("A Weir job stopped with an error", entry.Title);
        Assert.Equal(
            "{\"job_id\":1,\"job_kind\":\"processing.test.crash.v1\",\"failure_class\":\"unknown\"," +
            "\"message\":\"Weir job failed: The job hit an unexpected error. This job is marked failed so it does not look successful.\"," +
            "\"next_action\":\"Review this job and use Start again after fixing the cause.\",\"retry_scheduled\":false,\"result\":\"failed\"," +
            "\"relative_media_path\":\"Films/Caf\\u00e9.mkv\",\"library_id\":4,\"trigger\":\"webhook\",\"run_id\":\"r-1\"}",
            entry.Detail);
        Assert.Equal("failed", entry.Result);
        var recorded = Assert.Single(recorder.Failures);
        Assert.Equal((4L, "tv", "Films/Café.mkv"), (recorded.LibraryId, recorded.MediaScope, recorded.RelativeMediaPath));
        Assert.Equal(1L, _db.Count("SELECT count(*) FROM activity_events WHERE library_id = 4 AND relative_path = 'Films/Café.mkv' AND \"trigger\" = 'webhook' AND run_key = 'run:r-1'"));
    }

    [Fact]
    public async Task A_retry_decided_by_the_failure_policy_is_reported_as_retrying()
    {
        await _db.Store.EnqueueOrGetAsync("r", "processing.test.crash.v1", "{\"relative_media_path\": \"a.mkv\"}", maxAttempts: 1);
        var processor = _db.Processor([new DelegateHandler("processing.test.crash.v1", _ => throw new InvalidOperationException("x"))], new RecordingFailureRecorder(willRetry: true));

        await processor.ProcessOneAsync("w", now: T0);

        using var detail = JsonDocument.Parse(Assert.Single(_db.ActivityEvents()).Detail!);
        Assert.True(detail.RootElement.GetProperty("retry_scheduled").GetBoolean());
        Assert.Equal("retrying", detail.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_handler_that_recorded_its_own_failure_is_not_recorded_twice()
    {
        await _db.Store.EnqueueOrGetAsync("b", "processing.test.recorded.v1", "{\"relative_media_path\": \"a.mkv\"}", maxAttempts: 1);
        var recorder = new RecordingFailureRecorder(willRetry: null);
        var processor = _db.Processor(
            [new DelegateHandler("processing.test.recorded.v1", _ => throw new AlreadyRecordedFailureException("the output folder is not writable"))],
            recorder);

        await processor.ProcessOneAsync("w", now: T0);

        Assert.Empty(_db.ActivityEvents());
        Assert.Empty(recorder.Failures);
        Assert.Equal(
            "Weir job failed: The job hit an unexpected error. This job is marked failed so it does not look successful. Technical detail: AlreadyRecordedFailure: the output folder is not writable",
            (await _db.Store.GetAsync(1))!.LastError);
    }

    [Fact]
    public async Task Process_one_refuses_a_claimed_retired_row()
    {
        _db.InsertRawJob("legacy", "trimmer.radarr.cleanup_drive.v1");

        Assert.Equal(JobProcessOutcome.Processed, await _db.Processor([new DelegateHandler("processing.test.other.v1", _ => { })]).ProcessOneAsync("t", now: T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, row.Status);
        Assert.Equal(
            "Weir job failed: The job hit an unexpected error. Weir will try this job again shortly. Technical detail: RuntimeError: worker refused a retired job_kind: 'trimmer.radarr.cleanup_drive.v1' (row id=1); nothing runs this kind any more",
            row.LastError);
    }

    [Fact]
    public async Task A_leftover_pruner_row_is_refused_until_its_attempts_are_used()
    {
        _db.InsertRawJob("legacy-pruner", "pruner.candidate_removal.preview.v1", maxAttempts: 2);
        var processor = _db.Processor();

        await processor.ProcessOneAsync("t", now: T0);
        Assert.Contains("worker refused", (await _db.Store.GetAsync(1))!.LastError, StringComparison.Ordinal);
        await processor.ProcessOneAsync("t", now: T0.AddMinutes(1));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Failed, row.Status);
        Assert.Contains("marked failed", row.LastError, StringComparison.Ordinal);
        Assert.Empty(_db.ActivityEvents());
    }

    [Fact]
    public async Task Process_one_rejects_unprefixed_job_kind_row()
    {
        _db.InsertRawJob("legacy-unprefixed", "legacy.unprefixed");

        Assert.Equal(JobProcessOutcome.Processed, await _db.Processor([new DelegateHandler("processing.test.other.v1", _ => { })]).ProcessOneAsync("t", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, row.Status);
        Assert.Contains("processing.* prefix", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_with_no_handler_is_never_claimed()
    {
        await _db.Store.EnqueueOrGetAsync("d3", "processing.test.unknown.v1");

        Assert.Equal(JobProcessOutcome.Idle, await _db.Processor([new DelegateHandler("processing.test.other.v1", _ => { })]).ProcessOneAsync("w", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Claiming_every_kind_fails_a_job_with_no_handler()
    {
        await _db.Store.EnqueueOrGetAsync("d3", "processing.test.unknown.v1");

        Assert.Equal(JobProcessOutcome.Processed, await _db.Processor(claimAllKinds: true).ProcessOneAsync("w", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, row.Status);
        Assert.Contains("processing.test.unknown.v1", row.LastError, StringComparison.Ordinal);
        Assert.Contains("ProcessingNoHandlerForJobKind", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_one_survives_complete_claim_raises()
    {
        await _db.Store.EnqueueOrGetAsync("d-complete-raise", "processing.test.ok.v1", maxAttempts: 3);
        var processor = new ProcessingJobProcessorWithComplete(_db, (_, _, _) => throw new InvalidOperationException("db write failed")).Processor;

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.HandlerOkFinalizeFailed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.LeaseOwner);
        Assert.StartsWith("processing_terminalization_failure:", row.LastError, StringComparison.Ordinal);
        Assert.Contains("db write failed", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_one_complete_refused_triggers_terminalization()
    {
        await _db.Store.EnqueueOrGetAsync("d-complete-false", "processing.test.ok.v1", maxAttempts: 3);
        var processor = new ProcessingJobProcessorWithComplete(_db, (_, _, _) => Task.FromResult(false)).Processor;

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        var row = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.HandlerOkFinalizeFailed, row.Status);
        Assert.Equal(
            "processing_terminalization_failure: Weir job failed: The job hit an unexpected error. The work ran, but Weir could not record that it finished. Technical detail: RuntimeError: complete_claimed_processing_job refused (lease/state mismatch)",
            row.LastError);
    }

    [Fact]
    public async Task A_handler_that_gives_its_row_back_to_the_queue_ends_the_step_without_a_warning()
    {
        // A clean that finds its file in use puts its own row back to wait; the worker has nothing left to complete, and that is not a failure.
        await _db.Store.EnqueueOrGetAsync("postponed", "processing.test.ok.v1", maxAttempts: 3);
        var logger = new CapturingLogger<ProcessingJobProcessor>();
        var notifications = new RecordingNotifications();
        var processor = _db.Processor(
            [new DelegateHandler("processing.test.ok.v1", context =>
            {
                Assert.True(_db.Store.ReleaseClaimedAsync(context.Id, context.LeaseOwner, T0).GetAwaiter().GetResult());
                context.ReleasedLease = true;
            })],
            notifications: notifications,
            logger: logger);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Equal(ProcessingJobStatus.Pending, (await _db.Store.GetAsync(1))!.Status);
        Assert.DoesNotContain(logger.Messages, message => message.Level >= LogLevel.Warning);
        Assert.Empty(notifications.Sent);
    }

    [Fact]
    public async Task Terminalization_of_first_job_does_not_block_second_job_completion()
    {
        await _db.Store.EnqueueOrGetAsync("seq-a", "processing.test.ok.v1", maxAttempts: 3);
        await _db.Store.EnqueueOrGetAsync("seq-b", "processing.test.ok.v1", maxAttempts: 3);
        var processor = new ProcessingJobProcessorWithComplete(_db, (id, owner, now) => id == 1
            ? throw new InvalidOperationException("first job complete failed")
            : _db.Store.CompleteClaimedAsync(id, owner, now)).Processor;

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));
        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600, T0));

        Assert.Equal(ProcessingJobStatus.HandlerOkFinalizeFailed, (await _db.Store.GetAsync(1))!.Status);
        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(2))!.Status);
    }

    [Fact]
    public async Task A_long_handler_still_completes_against_the_claim_time()
    {
        // Complete is passed the claim-time `now`, so a handler outliving its lease still completes.
        await _db.Store.EnqueueOrGetAsync("slow", "processing.test.slow.v1");
        var processor = _db.Processor([new DelegateHandler("processing.test.slow.v1", _ => _db.Clock.SetUtcNow(T0.AddHours(3)))]);

        await processor.ProcessOneAsync("w", 300, T0);

        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task A_handler_stopped_by_shutdown_leaves_its_row_leased_for_recovery()
    {
        await _db.Store.EnqueueOrGetAsync("stop", "processing.test.stop.v1");
        using var stopping = new CancellationTokenSource();
        var processor = _db.Processor([new DelegateHandler("processing.test.stop.v1", async context =>
        {
            await stopping.CancelAsync();
            stopping.Token.ThrowIfCancellationRequested();
        })]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOneAsync("w", 300, T0, cancellationToken: stopping.Token));

        Assert.Equal(ProcessingJobStatus.Leased, (await _db.Store.GetAsync(1))!.Status);
    }

    [Fact]
    public async Task The_worker_claim_honours_the_schedule()
    {
        _db.SeedSuiteSettings();
        var library = _db.AddLibrary(scheduleGrid: new string('0', ScheduleGrid.SlotsPerWeek));
        await _db.Store.EnqueueOrGetAsync("gated", "processing.test.ok.v1", $"{{\"library_id\": {library}}}");

        Assert.Equal(JobProcessOutcome.Idle, await _db.Processor([new DelegateHandler("processing.test.ok.v1", _ => { })]).ProcessOneAsync("w", now: T0));
    }

    private sealed class ProcessingJobProcessorWithComplete(JobsTestDatabase db, Func<long, string, DateTimeOffset, Task<bool>> complete)
    {
        public ProcessingJobProcessor Processor { get; } = new(
            db.Store,
            new JobHandlerRegistry([new DelegateHandler("processing.test.ok.v1", _ => { })]),
            new SqliteActivityWriter(db.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            db.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingJobProcessor>.Instance)
        {
            CompleteOverride = complete,
        };
    }

    private sealed class RecordingNotifications : IJobNotifications
    {
        public List<string> Sent { get; } = [];

        public void Dispatch(string moduleName, string eventKind, long jobId, string jobKind, bool willRetry = false) =>
            Sent.Add($"{eventKind} {jobId} {jobKind} willRetry={willRetry}");

        public void DispatchFailedForGood(string moduleName, long jobId, string jobKind) =>
            Sent.Add($"failed-for-good {jobId} {jobKind}");
    }

    private sealed class RecordingFailureRecorder(bool? willRetry) : IUnhandledJobFailureRecorder
    {
        public List<UnhandledJobFailure> Failures { get; } = [];

        public Task<bool?> RecordAsync(UnhandledJobFailure failure, CancellationToken cancellationToken)
        {
            Failures.Add(failure);
            return Task.FromResult(willRetry);
        }
    }

    [Theory]
    [InlineData("not json", "")]
    [InlineData("{}", "")]
    [InlineData("{\"library_id\": \"abc\"}", "")]
    [InlineData("{\"library_id\": true}", ",\"library_id\":true")]
    public async Task An_unrecorded_handler_failure_names_a_workflow_only_when_the_payload_gives_one(string payload, string libraryPart)
    {
        await _db.Store.EnqueueOrGetAsync("c", "processing.test.crash.v1", payload, maxAttempts: 1);
        var processor = _db.Processor([new DelegateHandler("processing.test.crash.v1", _ => throw new InvalidOperationException("x"))], new RecordingFailureRecorder(willRetry: null));

        await processor.ProcessOneAsync("w", now: T0);

        Assert.Equal(
            "{\"job_id\":1,\"job_kind\":\"processing.test.crash.v1\",\"failure_class\":\"unknown\"," +
            "\"message\":\"Weir job failed: The job hit an unexpected error. This job is marked failed so it does not look successful.\"," +
            "\"next_action\":\"Review this job and use Start again after fixing the cause.\",\"retry_scheduled\":false,\"result\":\"failed\"" + libraryPart + "}",
            Assert.Single(_db.ActivityEvents()).Detail);
    }

    [Fact]
    public async Task A_job_row_with_no_attempt_limit_runs_as_a_job_with_one_attempt()
    {
        _db.InsertRawJob("raw", "processing.test.ok.v1", maxAttempts: 0);
        JobWorkContext? seen = null;
        var processor = _db.Processor([new DelegateHandler("processing.test.ok.v1", context => seen = context)]);

        await processor.ProcessOneAsync("w", 3600, T0);

        Assert.Equal((1, 1), (seen!.AttemptCount, seen.MaxAttempts));
    }
}
