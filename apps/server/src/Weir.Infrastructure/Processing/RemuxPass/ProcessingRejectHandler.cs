using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// Worker handler for <c>processing.file.reject.v1</c>: tell the media
/// manager a release Weir could not process is bad, so it can find a different one (#465, #471).
/// </summary>
/// <remarks>
/// <para>
/// <b>Report first, remove second.</b> Nothing is removed until the manager has accepted the report (a 2xx answer); "could
/// not reach it" and "it refused" are treated the same: not accepted.
/// </para>
/// <para>
/// <b>Anything short of certainty falls back to pass-through</b> — never to deleting a file nobody was told about, and
/// never to silently doing nothing. The fallback is recorded in Activity with the reason.
/// A file Weir could not read is the one exception: it is never handed back as if it were good, so it is left where it is, rejected.
/// </para>
/// <para>
/// Fix #532: whichever way the attempt ends, a Files row is upserted (not merely updated) so a rejection that no scan
/// had seen still appears on the Files screen.
/// </para>
/// </remarks>
public sealed partial class ProcessingRejectHandler : IJobHandler
{
    private readonly SqliteDatabase _database;
    private readonly IMediaManagerPorts _ports;
    private readonly MediaManagerConnectionService _connections;
    private readonly HandoffCompletionReporter _reporter;
    private readonly RejectRoutes _routes;
    private readonly HandoffLedgerStore _ledger;
    private readonly ProcessingJobStore _jobs;
    private readonly RejectPacing _pacing;
    private readonly LibraryStore _libraries;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessingRejectHandler> _logger;

    public ProcessingRejectHandler(
        SqliteDatabase database,
        IMediaManagerPorts ports,
        MediaManagerConnectionService connections,
        HandoffCompletionReporter reporter,
        RejectRoutes routes,
        HandoffLedgerStore ledger,
        ProcessingJobStore jobs,
        RejectPacing pacing,
        LibraryStore libraries,
        TimeProvider time,
        ILogger<ProcessingRejectHandler> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _pacing = pacing ?? throw new ArgumentNullException(nameof(pacing));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string JobKind => IntakeRules.RejectJobKind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = FollowUpJobPayload.Parse(context.PayloadJson);
        var relativePath = FollowUpJobPayload.RelativeMediaPath(payload);
        var libraryId = FollowUpJobPayload.LibraryId(payload);
        if (relativePath.Length == 0 || libraryId is null)
        {
            throw new InvalidOperationException("A reject job needs a file and a workflow.");
        }

        var originRaw = FollowUpJobPayload.Origin(payload);
        var reason = payload.Get("reason") is WireString reasonValue && reasonValue.Value.Length > 0
            ? reasonValue.Value
            : "Weir could not process this file.";
        var failureClass = payload.Get("failure_class") is WireString failureClassValue ? failureClassValue.Value : null;
        var unreadable = payload.Get("rejection_kind") is WireString { Value: RejectionKinds.UnreadableFile };
        var origin = originRaw is not null ? HandoffOrigin.FromPayload(new WireObject().Set("origin", originRaw)) : null;

        // 1. Everything the attempt needs, read with the unit of work closed before any network call.
        string watchedRoot = string.Empty;
        HandoffReportTarget? target = null;
        string? targetRefusalReason = null;
        var queueConnections = new List<ManagerConnection>();
        var links = WorkflowManagerLinks.None;
        var alreadyAccepted = false;
        await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, libraryId, null).ConfigureAwait(false);
                if (library is null)
                {
                    throw new InvalidOperationException($"Workflow {libraryId} no longer exists.");
                }

                watchedRoot = RemuxPassPaths.Resolve(library.WatchedFolder);
                links = await _libraries.ManagerLinksAsync(uow, library.Id).ConfigureAwait(false);
                alreadyAccepted = origin is { HandoffId.Length: > 0 } &&
                    await HandoffLedgerStore.FindAsync(uow, origin.SourceKey, origin.HandoffId).ConfigureAwait(false) is { ReportedStatus: HandoffLedgerRules.Rejected };
                if (origin is not null && _ports.PortForKind(origin.SourceKey) is { } originPort && !originPort.Capabilities().RemovesQueueItems)
                {
                    (target, targetRefusalReason) = await _reporter.ResolveHandoffTargetAsync(uow, origin).ConfigureAwait(false);
                }

                var connectionIds = await _libraries.ManagerConnectionIdsAsync(uow, library.Id).ConfigureAwait(false);
                var linked = await _connections.ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false);
                queueConnections = [.. linked.Where(connection => _ports.PortForKind(connection.Kind)?.Capabilities().RemovesQueueItems == true)];
            },
            _logger,
            "reject claim",
            cancellationToken).ConfigureAwait(false);

        var source = RemuxPassPaths.Resolve(Path.Combine(watchedRoot, relativePath));

        // 2. The attempt, paced, with no unit of work open.
        await _pacing.WaitAsync(cancellationToken).ConfigureAwait(false);
        RejectRouteOutcome attempt;
        if (alreadyAccepted)
        {
            // The manager has this rejection already: a job that runs again finishes it and never reports it a second time.
            attempt = RejectRoutes.AfterAcceptedRejection(target?.Connection.Label ?? "The media manager", source, watchedRoot, links);
        }
        else if (origin is not null)
        {
            attempt = target is not null
                ? await _routes.ThroughHandoffAsync(
                    target, origin, source, watchedRoot, reason, failureClass, links, cancellationToken,
                    (report, token) => RecordRejectionAcceptedAsync(origin, report, relativePath, token)).ConfigureAwait(false)
                : targetRefusalReason is not null
                    ? new RejectRouteOutcome(false, $"Weir could not report to the manager: {targetRefusalReason}.")
                    : queueConnections.Count > 0
                        ? await _routes.ThroughQueueAsync(queueConnections, source, cancellationToken).ConfigureAwait(false)
                        : new RejectRouteOutcome(false, "No linked media manager can take a rejection for this file, so Weir handed the original back instead.");
        }
        else if (queueConnections.Count > 0)
        {
            attempt = await _routes.ThroughQueueAsync(queueConnections, source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            attempt = new RejectRouteOutcome(false, "No linked media manager can take a rejection for this file, so Weir handed the original back instead.");
        }

        // A manager that did not answer may answer the next time, and a release known to be bad is not handed back as good because a
        // manager was busy for a moment. Until the attempts run out the job is retried like any other transient failure; the file is
        // handed back, or left where it is when it could not be read, only when no manager can take the rejection or the last attempt failed too.
        if (!attempt.Done && attempt.Transient && context.AttemptCount < context.MaxAttempts)
        {
            throw new InvalidOperationException(attempt.Reason);
        }

        // 3. Bookkeeping, and the fallback, in one short transaction.
        var now = _time.GetUtcNow();
        await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                if (attempt.Report is { } report)
                {
                    await HandoffCompletionReporter.RecordHandoffReportAsync(uow, report.Target, report.Body, report.Delivery, relativePath).ConfigureAwait(false);
                }

                var detail = new WireObject()
                    .Set("job_id", context.Id)
                    .Set("relative_media_path", relativePath)
                    .Set("library_id", libraryId)
                    .Set("manager", attempt.Manager)
                    .Set("message", attempt.Reason)
                    .Set("trigger", "worker")
                    .Set("result", attempt.Done ? "success" : "warning");
                foreach (var (key, value) in attempt.DetailOrEmpty.Items)
                {
                    detail.Set(key, value);
                }

                string eventType;
                string title;
                if (attempt.Done)
                {
                    // Fix #532: always upsert, even when no scan has recorded this file yet. The reason keeps why
                    // the release was rejected (from the job payload — e.g. "no retainable audio") alongside what
                    // happened to the download, rather than the acceptance sentence alone replacing it.
                    var rejectedReason = WireStrings.Slice(WireStrings.Strip($"{reason} {attempt.Reason}"), 10_000);
                    // #785: no fingerprint for a source the reject route already removed — there is nothing left to protect.
                    await RemuxPassFileState.UpsertRejectedAsync(uow, libraryId.Value, relativePath, rejectedReason, failureClass, File.Exists(source) ? source : null)
                        .ConfigureAwait(false);
                    if (origin is not null)
                    {
                        await _ledger.RecordOutcomeAsync(uow, origin.SourceKey, origin.HandoffId, HandoffLedgerRules.Rejected, null, attempt.Reason).ConfigureAwait(false);
                    }

                    eventType = ActivityEventTypes.ProcessingFileRejected;
                    title = $"{MediaPathNames.Name(relativePath, OperatingSystem.IsWindows())} was rejected so a different release can be found";
                }
                else if (unreadable)
                {
                    // A file Weir could not read is never handed on as if it were good: with no manager to take the rejection it stays where
                    // it is, rejected, for a person to decide.
                    var keptReason = WireStrings.Slice(WireStrings.Strip($"{reason} {ToolFailureText.UnreadableKept}"), 10_000);
                    await RemuxPassFileState.UpsertRejectedAsync(uow, libraryId.Value, relativePath, keptReason, failureClass, File.Exists(source) ? source : null)
                        .ConfigureAwait(false);
                    eventType = ActivityEventTypes.ProcessingFileRejected;
                    title = $"{MediaPathNames.Name(relativePath, OperatingSystem.IsWindows())} could not be reported as unwanted, so it was left where it is";
                    detail.Set("next_action", "Check the file, and remove it yourself if you do not want it.");
                }
                else
                {
                    var liveLibrary = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, libraryId, null).ConfigureAwait(false);
                    if (liveLibrary is not null)
                    {
                        await EnqueuePassThroughFallbackAsync(uow, liveLibrary, relativePath, originRaw).ConfigureAwait(false);
                    }

                    await RemuxPassFileState.MarkFileStatusAsync(
                        uow, libraryId.Value, relativePath, ProcessingFileStatuses.ProcessingFailed,
                        WireStrings.Slice($"{reason} {attempt.Reason} Weir is handing the original back unchanged instead.", 10_000),
                        now).ConfigureAwait(false);
                    // #785: the original is untouched here, so its current bytes are exactly the fingerprint to protect.
                    await RemuxPassFileState.RecordFingerprintFromPathAsync(uow, libraryId.Value, relativePath, File.Exists(source) ? source : null).ConfigureAwait(false);
                    eventType = ActivityEventTypes.ProcessingFileRejectFellBack;
                    title = $"{MediaPathNames.Name(relativePath, OperatingSystem.IsWindows())} could not be rejected, so it is being handed back";
                    detail.Set("next_action", "Weir queued the original to be handed back unchanged.");
                }

                await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                    eventType, "processing", title, WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000))).ConfigureAwait(false);
            },
            _logger,
            "reject bookkeeping",
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Reject for {Path}: {Reason}", relativePath, attempt.Reason);
    }

    /// <summary>
    /// Saves that the manager accepted the rejection, in its own short transaction as soon as it has: the hand-off notes it, so a run of this job
    /// after any later failure does not report it again, and Activity says the manager was told.
    /// </summary>
    private Task RecordRejectionAcceptedAsync(HandoffOrigin origin, RejectDeliveryReport report, string relativePath, CancellationToken cancellationToken) =>
        LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                if (!string.IsNullOrEmpty(origin.HandoffId))
                {
                    await HandoffLedgerStore.RecordRejectionReportedAsync(uow, origin.SourceKey, origin.HandoffId).ConfigureAwait(false);
                }

                await HandoffCompletionReporter.RecordHandoffReportAsync(uow, report.Target, report.Body, report.Delivery, relativePath).ConfigureAwait(false);
            },
            _logger,
            "reject accepted",
            cancellationToken);

    /// <summary>Queues a pass-through, called unconditionally on any reject failure — the reject route never
    /// re-checks the library's failure policy: anything short of certainty always falls back to pass-through.</summary>
    private async Task EnqueuePassThroughFallbackAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath, WireObject? origin)
    {
        var body = new WireObject().Set("relative_media_path", relativePath).Set("library_id", library.Id).Set("trigger", "worker");
        if (origin is { IsTruthy: true })
        {
            body.Set("origin", origin);
        }

        // The fingerprint stats the file on the watched folder, which may be a network share. Work the
        // dedupe key out before asking for the transaction, not in the argument list after it: arguments
        // evaluate left to right, so inlining this would put a remote stat inside the write lock
        // (#586 made that lock start at BEGIN). QueueingFailurePolicy.Enqueue's callers do the same.
        var dedupeKey = $"{IntakeRules.PassThroughJobKind}:{library.Id}:{relativePath}:{QueueingFailurePolicy.FingerprintTag(library, relativePath)}";
        await _jobs.EnqueueOrGetAsync(
            uow,
            dedupeKey,
            IntakeRules.PassThroughJobKind,
            WireJsonWriter.Dumps(body, WireJsonFormat.Compact),
            JobQueueRules.DefaultMaxAttempts,
            0,
            (int)Math.Clamp(library.Priority, int.MinValue, int.MaxValue)).ConfigureAwait(false);
    }
}
