using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.Observability;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// One worker pass: claim at most one job under the current admission, run its handler, then complete
/// or fail it.
/// </summary>
public sealed class ProcessingJobProcessor
{
    public const int DefaultLeaseSeconds = 300;
    public const string TerminalizationFailurePrefix = "processing_terminalization_failure: ";
    public const string Module = "Weir";

    private readonly ProcessingJobStore _queue;
    private readonly JobHandlerRegistry _handlers;
    private readonly IActivityWriter _activity;
    private readonly IUnhandledJobFailureRecorder _failureRecorder;
    private readonly IJobNotifications _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly PeriodicTaskRegistry? _tasks;
    private readonly DataChangePublisher? _changes;
    private readonly ProcessPulse? _pulse;

    public ProcessingJobProcessor(
        ProcessingJobStore queue,
        JobHandlerRegistry handlers,
        IActivityWriter activity,
        IUnhandledJobFailureRecorder failureRecorder,
        IJobNotifications notifications,
        TimeProvider time,
        ILogger<ProcessingJobProcessor> logger,
        PeriodicTaskRegistry? tasks = null,
        DataChangePublisher? changes = null,
        ProcessPulse? pulse = null)
    {
        _pulse = pulse;
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(failureRecorder);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _handlers = handlers;
        _activity = activity;
        _failureRecorder = failureRecorder;
        _notifications = notifications;
        _time = time;
        _logger = logger;
        _tasks = tasks;
        _changes = changes;
        Kinds = ClaimableKinds.For(handlers);
    }

    /// <summary>
    /// The kinds this processor claims. Defaults to the registry's kinds plus refused kinds; null claims
    /// every kind (used by tests).
    /// </summary>
    public ClaimableKinds? Kinds { get; init; }

    /// <summary>Test seam replacing <see cref="ProcessingJobStore.CompleteClaimedAsync"/>.</summary>
    internal Func<long, string, DateTimeOffset, Task<bool>>? CompleteOverride { get; init; }

    /// <summary>Test seam replacing <see cref="ProcessingJobStore.RenewLeaseAsync"/>: job id, lease owner, the new expiry.</summary>
    internal Func<long, string, DateTimeOffset, Task<bool>>? RenewOverride { get; init; }

    /// <summary>
    /// Test seam: called each time the lease-renewal heartbeat has started waiting for its next tick, which
    /// is after any previous renewal has been written. A test on a fake clock waits for it between advances
    /// instead of sleeping and hoping the renewal landed and the next timer was registered.
    /// </summary>
    internal Action? LeaseRenewalWaiting { get; init; }

    public async Task<JobProcessOutcome> ProcessOneAsync(
        string leaseOwner,
        int leaseSeconds = DefaultLeaseSeconds,
        DateTimeOffset? now = null,
        WorkLane lane = WorkLane.Files,
        ClaimableKinds? kinds = null,
        CancellationToken cancellationToken = default)
    {
        var when = Timestamp.TruncateToMicroseconds((now ?? _time.GetUtcNow()).ToUniversalTime());
        var leaseUntil = when + TimeSpan.FromSeconds(leaseSeconds);

        // The schedule and the pause are evaluated at lease time, not only at enqueue (#337). A job
        // already leased is left to finish. A caller running the upkeep lane (#717) passes its own kinds and
        // lane, so a scan or sweep is never gated by the files-at-once limit it is exempt from.
        var job = await _queue.ClaimNextAdmittedAsync(leaseOwner, leaseUntil, when, kinds ?? Kinds, lane, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobProcessOutcome.Idle;
        }

        // The claim refuses work while paused. This asks the pause once more, separately, so that a job claimed by mistake
        // goes back untouched, with its attempt given back, instead of reaching a handler that changes a file.
        if (await _queue.PauseForbidsAsync(job.JobKind, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError(
                "Weir claimed job_id={JobId} kind={JobKind} while processing is paused, so it was put back without running.",
                job.Id,
                job.JobKind);
            await _queue.ReleaseClaimedAsync(job.Id, leaseOwner, when, CancellationToken.None).ConfigureAwait(false);
            return JobProcessOutcome.Idle;
        }

        var context = new JobWorkContext(
            job.Id,
            job.JobKind,
            job.PayloadJson,
            leaseOwner,
            job.AttemptCount,
            job.MaxAttempts == 0 ? 1 : job.MaxAttempts);
        var willRetry = WorkerFailures.RetryComing(context.AttemptCount, context.MaxAttempts);

        if (JobKindGuard.IsRetired(context.JobKind))
        {
            await FailQuietlyAsync(
                context,
                WorkerFailures.RefusedJobError(Module, WorkerFailures.RetiredKindReason(context.JobKind, context.Id), willRetry),
                when,
                "fail_claimed after retired job_kind guard job_id={JobId}").ConfigureAwait(false);
            return JobProcessOutcome.Processed;
        }

        if (!JobKindGuard.HasProcessingPrefix(context.JobKind))
        {
            await FailQuietlyAsync(
                context,
                WorkerFailures.RefusedJobError(Module, WorkerFailures.UnprefixedKindReason(context.JobKind, context.Id), willRetry),
                when,
                "fail_claimed after processing.* prefix guard job_id={JobId}").ConfigureAwait(false);
            return JobProcessOutcome.Processed;
        }

        var handler = _handlers.Find(context.JobKind);
        if (handler is null)
        {
            await FailQuietlyAsync(
                context,
                WorkerFailures.StoredError(WorkerFailures.JobFailure(Module, WorkerFailures.NoHandler(context.JobKind), willRetry)),
                when,
                "fail_claimed_processing_job failed after missing handler job_id={JobId}").ConfigureAwait(false);
            return JobProcessOutcome.Processed;
        }

        var taskKey = ScheduledJobTasks.KeyFor(context.JobKind, context.PayloadJson);
        ReportRunStarted(taskKey);
        try
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["job_id"] = context.Id }))
            {
                await RunHandlerWithLeaseRenewalAsync(handler, context, leaseSeconds, leaseUntil, cancellationToken).ConfigureAwait(false);
            }

            ReportRunEnded(taskKey, ok: true, why: null);
        }
        catch (LeaseLostException lost)
        {
            // The row is left as it is: its lease has run out, so recovery or another worker takes the job from here, and this
            // worker has stopped running it. Failing or completing it now could only be refused or undo that worker's claim.
            if (WasAway(lost.Since))
            {
                _logger.LogInformation(
                    "Weir was not running, or the computer was asleep, while it worked on {Work}; the job was stopped and will be picked up again. job_id={JobId}",
                    WorkOn(context),
                    context.Id);
            }
            else
            {
                _logger.LogWarning("{Message} The job was stopped and is left for recovery. job_id={JobId}", lost.Message, context.Id);
            }

            ReportRunEnded(taskKey, ok: false, why: "lease_lost");
            return JobProcessOutcome.Processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row stays leased and startup recovery requeues it.
            throw;
        }
#pragma warning disable CA1031 // A handler failure of any kind fails the job; the worker must survive it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var failure = WorkerFailures.JobFailure(Module, FailureMessages.FromDotNet(exception), willRetry, ToolFailureText.KnownReason(exception));
            _logger.LogError(
                exception,
                "Job handler failed for job_id={JobId} kind={JobKind}: {Message}",
                context.Id,
                context.JobKind,
                failure.Message);
            ReportRunEnded(taskKey, ok: false, failure.Why);
            if (exception is not AlreadyRecordedFailureException)
            {
                // A handler that recorded its own failure has said it once already (#488).
                await RecordUnhandledFailureAsync(context, failure.Message).ConfigureAwait(false);
            }

            await FailQuietlyAsync(
                context,
                WorkerFailures.StoredError(failure),
                when,
                "fail_claimed_processing_job failed after handler error job_id={JobId}").ConfigureAwait(false);
            _notifications.Dispatch("processing", "failed", context.Id, context.JobKind, willRetry);
            PublishLibraryScanEnded(context.JobKind);
            return JobProcessOutcome.Processed;
        }

        // A handler that put its own row back in the queue (a clean that finds its file in use, or the drive short of room) has nothing left
        // to complete: the row is pending again, not finished, and that is not a failure to record.
        if (context.ReleasedLease)
        {
            PublishLibraryScanEnded(context.JobKind);
            return JobProcessOutcome.Processed;
        }

        var completeOk = true;
        string? completeError = null;
        try
        {
            var ok = CompleteOverride is not null
                ? await CompleteOverride(context.Id, context.LeaseOwner, when).ConfigureAwait(false)
                : await _queue.CompleteClaimedAsync(context.Id, context.LeaseOwner, when, CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                completeOk = false;
                completeError = "complete_claimed_processing_job refused (lease/state mismatch)";
            }
        }
#pragma warning disable CA1031 // Recording completion failed; the row is terminalised below instead.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            completeOk = false;
            _logger.LogError(exception, "complete_claimed_processing_job failed job_id={JobId}", context.Id);
            completeError = exception.Message;
        }

        if (completeOk)
        {
            switch (context.Alert)
            {
                case JobAlert.Finished:
                    _notifications.Dispatch("processing", "completed", context.Id, context.JobKind);
                    break;
                case JobAlert.FailedForGood:
                    _notifications.DispatchFailedForGood("processing", context.Id, context.JobKind);
                    break;
            }

            PublishLibraryScanEnded(context.JobKind);
            return JobProcessOutcome.Processed;
        }

        // The handler finished; only recording that failed. Said plainly, with the detail after it.
        var bounded = WireStrings.Slice(
            TerminalizationFailurePrefix + WorkerFailures.StoredError(
                FailureMessages.FromException(
                    Module,
                    "job",
                    FailureMessages.RuntimeError(completeError!),
                    continuation: "The work ran, but Weir could not record that it finished.")),
            JobQueueRules.LastErrorLimit);
        try
        {
            var recovered = await _queue.FailLeasedAfterCompleteFailureAsync(context.Id, context.LeaseOwner, bounded, when, CancellationToken.None)
                .ConfigureAwait(false);
            if (!recovered)
            {
                _logger.LogWarning(
                    "Terminalization recovery did not apply job_id={JobId} owner={Owner}",
                    context.Id,
                    context.LeaseOwner);
            }
        }
#pragma warning disable CA1031 // The last resort after a failed completion is logged; the worker carries on and the lease expires.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "fail_leased_processing_job_after_complete_failure failed job_id={JobId}", context.Id);
        }

        PublishLibraryScanEnded(context.JobKind);
        return JobProcessOutcome.Processed;
    }

    /// <summary>
    /// A library scan that has ended, or failed and waits for its retry, changes what the Library screen shows. The scan says so
    /// while it indexes its files, but only here is its job row final, so a screen that reads again now sees the scan as over.
    /// </summary>
    private void PublishLibraryScanEnded(string jobKind)
    {
        if (jobKind == LibraryModeJobKinds.ScanKind)
        {
            _changes?.Publish(DataTopics.LibraryScan);
        }
    }

    private void ReportRunStarted(string? taskKey)
    {
        if (taskKey is not null)
        {
            _tasks?.Begin(taskKey);
        }
    }

    private void ReportRunEnded(string? taskKey, bool ok, string? why)
    {
        if (taskKey is not null)
        {
            _tasks?.End(taskKey, ok, why);
        }
    }

    /// <summary>
    /// Run the handler while a background heartbeat renews its lease roughly every
    /// <paramref name="leaseSeconds"/> / 3 seconds (#540 item 1), so a handler that runs longer than one
    /// lease (a remux longer than 300 s, say) keeps its row leased instead of letting a second worker claim
    /// it and start a second ffmpeg process on the same file. Completion and failure verify the lease owner
    /// themselves (<see cref="ProcessingJobStore.CompleteClaimedAsync"/> and
    /// <see cref="ProcessingJobStore.FailClaimedAsync"/>); this only keeps the lease alive while the
    /// handler is still running. When renewals fail against a busy database until the lease has run out, the handler is
    /// cancelled and <see cref="LeaseLostException"/> is thrown, because another worker may claim the job from that moment.
    /// </summary>
    private async Task RunHandlerWithLeaseRenewalAsync(
        IJobHandler handler, JobWorkContext context, int leaseSeconds, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var expiry = new LeaseExpiry(_time, leaseUntil, handlerCts, leaseUntil - TimeSpan.FromSeconds(leaseSeconds));
        var heartbeat = RenewLeaseHeartbeatAsync(context, leaseSeconds, expiry, renewalCts.Token);
        try
        {
            await handler.HandleAsync(context, handlerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.Lost && !cancellationToken.IsCancellationRequested)
        {
            throw new LeaseLostException(context.Id, expiry.Since);
        }
        finally
        {
            await renewalCts.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    /// <summary>The job's lease ran out while its renewals could not reach a busy database, so another worker may now hold it.</summary>
    private sealed class LeaseLostException(long jobId, DateTimeOffset since) : Exception($"The lease on job {jobId} ran out before it could be renewed.")
    {
        /// <summary>When the lease was last confirmed: the claim, or the last renewal the database accepted.</summary>
        public DateTimeOffset Since { get; } = since;
    }

    /// <summary>
    /// Whether the process could not run for most of the time since <paramref name="since"/>: the computer slept, a machine was
    /// saved or paused, a container was frozen. A lease that ran out then was not neglected, and a person has nothing to do about it.
    /// </summary>
    private bool WasAway(DateTimeOffset since)
    {
        var now = _time.GetUtcNow();
        return _pulse is not null && _pulse.AwayWithin(since, now).Within * 2 >= now - since;
    }

    /// <summary>What a person knows the job by: the file it works on when its payload names one, else just "a background job".</summary>
    private static string WorkOn(JobWorkContext context) =>
        JobPayload.StringProperty(JobPayload.ParseObject(context.PayloadJson), "relative_media_path") is { Length: > 0 } path
            ? MediaPathNames.Name(path, windows: true)
            : "a background job";

    /// <summary>
    /// Cancels the handler once the last lease the database confirmed has run out. A timer on the clock, not a check in the
    /// renewal loop, because a renewal waiting on a busy database can itself outlast the lease.
    /// </summary>
    private sealed class LeaseExpiry : IDisposable
    {
        private readonly TimeProvider _time;
        private readonly ITimer _timer;
        private volatile bool _lost;
        private long _sinceTicks;

        public LeaseExpiry(TimeProvider time, DateTimeOffset expiry, CancellationTokenSource handler, DateTimeOffset since)
        {
            _time = time;
            _sinceTicks = since.UtcTicks;
            _timer = time.CreateTimer(
                _ =>
                {
                    _lost = true;
                    handler.Cancel();
                },
                null,
                Until(expiry),
                Timeout.InfiniteTimeSpan);
        }

        public bool Lost => _lost;

        /// <summary>When the database last confirmed the lease: the claim, or the last renewal it accepted.</summary>
        public DateTimeOffset Since => new(Volatile.Read(ref _sinceTicks), TimeSpan.Zero);

        /// <summary>The database confirmed, at <paramref name="at"/>, a lease that runs to <paramref name="expiry"/>.</summary>
        public void Confirmed(DateTimeOffset at, DateTimeOffset expiry)
        {
            Volatile.Write(ref _sinceTicks, at.UtcTicks);
            _timer.Change(Until(expiry), Timeout.InfiniteTimeSpan);
        }

        /// <summary>The lease is no longer ours to watch: it was taken, or renewing failed for a reason other than a busy database.</summary>
        public void Disarm() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public void Dispose() => _timer.Dispose();

        private TimeSpan Until(DateTimeOffset expiry) => TimeSpan.FromTicks(Math.Max(0, (expiry - _time.GetUtcNow()).Ticks));
    }

    /// <summary>The lease-renewal heartbeat itself: sleep, renew, repeat, until cancelled.</summary>
    private async Task RenewLeaseHeartbeatAsync(JobWorkContext context, int leaseSeconds, LeaseExpiry leaseExpiry, CancellationToken cancellationToken)
    {
        var jobId = context.Id;
        var leaseOwner = context.LeaseOwner;
        var interval = TimeSpan.FromSeconds(Math.Max(0.05, leaseSeconds / 3.0));
        try
        {
            while (true)
            {
                var tick = Task.Delay(interval, _time, cancellationToken);
                LeaseRenewalWaiting?.Invoke();
                await tick.ConfigureAwait(false);
                var now = _time.GetUtcNow();
                bool renewed;
                try
                {
                    var expiry = now + TimeSpan.FromSeconds(leaseSeconds);
                    renewed = RenewOverride is not null
                        ? await RenewOverride(jobId, leaseOwner, expiry).ConfigureAwait(false)
                        : await _queue.RenewLeaseAsync(jobId, leaseOwner, expiry, now, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (LockedWrites.IsLock(exception))
                {
                    // A busy database must not end the renewals of a job that may run for hours: the lease has two more
                    // intervals to run, and the next renewal tries again. If the last confirmed lease runs out first, the
                    // expiry timer stops the handler.
                    _logger.LogWarning(exception, "Lease renewal could not reach the database job_id={JobId} owner={Owner}; trying again.", jobId, leaseOwner);
                    continue;
                }

                if (renewed)
                {
                    leaseExpiry.Confirmed(now, now + TimeSpan.FromSeconds(leaseSeconds));
                }
                else
                {
                    var since = leaseExpiry.Since;
                    leaseExpiry.Disarm();
                    if (WasAway(since))
                    {
                        _logger.LogInformation(
                            "Weir was not running, or the computer was asleep, while it worked on {Work}; the job's claim ran out in the meantime. job_id={JobId}",
                            WorkOn(context),
                            jobId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Lease renewal found the lease no longer held job_id={JobId} owner={Owner}; no longer renewing.",
                            jobId,
                            leaseOwner);
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The handler finished, or the worker is shutting down: stop renewing either way.
        }
#pragma warning disable CA1031 // A renewal-loop crash must never take the handler down with it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            leaseExpiry.Disarm();
            _logger.LogError(exception, "Lease renewal loop crashed job_id={JobId} owner={Owner}", jobId, leaseOwner);
        }
    }

    /// <summary>
    /// Record an unhandled failure: the file-state policy (#522) and one <c>processing.worker_failure</c>
    /// Activity entry.
    /// </summary>
    private async Task RecordUnhandledFailureAsync(JobWorkContext context, string message)
    {
        var payload = JobPayload.ParseObject(context.PayloadJson);
        var relative = JobPayload.StringProperty(payload, "relative_media_path");
        var relativeTrimmed = relative is not null && relative.Trim().Length > 0 ? relative.Trim() : null;
        var scope = JobPayload.StringProperty(payload, "media_scope") is "movie" or "tv" ? JobPayload.StringProperty(payload, "media_scope")! : "movie";
        var libraryIdValue = payload is { } element && element.TryGetProperty("library_id", out var raw) &&
                             (raw.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ||
                              JobPayload.LooseInteger(payload, "library_id") is not null)
            ? raw
            : (System.Text.Json.JsonElement?)null;
        var safeMessage = WireStrings.Slice(string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 1200);

        try
        {
            var willRetry = await _failureRecorder.RecordAsync(
                new UnhandledJobFailure(context, JobPayload.LooseInteger(payload, "library_id"), scope, relativeTrimmed, safeMessage),
                CancellationToken.None).ConfigureAwait(false);

            var detail = new WireObject()
                .Set("job_id", context.Id)
                .Set("job_kind", context.JobKind)
                .Set("failure_class", "unknown")
                .Set("message", safeMessage)
                .Set("next_action", "Review this job and use Start again after fixing the cause.")
                .Set("retry_scheduled", willRetry == true)
                .Set("result", willRetry == true ? "retrying" : "failed");
            if (relativeTrimmed is not null)
            {
                detail.Set("relative_media_path", relativeTrimmed);
            }

            if (libraryIdValue is { } libraryId)
            {
                detail.Set("library_id", WireJsonParser.Parse(libraryId.GetRawText()));
            }

            AddProvenance(detail, payload);
            await _activity.RecordAsync(
                new ActivityEventDraft(ActivityEventTypes.ProcessingWorkerFailure, "processing", "A Weir job stopped with an error", WireJsonWriter.Dumps(detail, WireJsonFormat.Compact)),
                CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Diagnostics must never stop the worker from failing the job.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "Failure diagnostics could not be persisted job_id={JobId}", context.Id);
        }
    }

    /// <summary>Copy <c>trigger</c> and <c>run_id</c> from the payload, only when present and valid.</summary>
    internal static void AddProvenance(WireObject detail, System.Text.Json.JsonElement? payload)
    {
        if (payload is not { } element)
        {
            return;
        }

        var trigger = JobPayload.StringProperty(payload, "trigger");
        if (trigger is not null && ActivityClassifier.Triggers.Contains(trigger.Trim().ToLowerInvariant()))
        {
            detail.Set("trigger", trigger.Trim().ToLowerInvariant());
        }

        if (element.TryGetProperty("run_id", out var runId))
        {
            var valid = runId.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => runId.GetString()!.Trim().Length > 0,
                System.Text.Json.JsonValueKind.Number => JobPayload.StrictInteger(payload, "run_id") is not null,
                _ => false,
            };
            if (valid)
            {
                detail.Set("run_id", WireJsonParser.Parse(runId.GetRawText()));
            }
        }
    }

    private async Task FailQuietlyAsync(JobWorkContext context, string errorText, DateTimeOffset when, string logMessage)
    {
        try
        {
            await _queue.FailClaimedAsync(context.Id, context.LeaseOwner, errorText, when, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Log and carry on: the worker must survive a failed write.
        catch (Exception exception)
#pragma warning restore CA1031
        {
#pragma warning disable CA2254 // The message templates are constants chosen by the caller.
            _logger.LogError(exception, logMessage, context.Id);
#pragma warning restore CA2254
        }
    }
}
