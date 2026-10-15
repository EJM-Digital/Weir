using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// Read-only <c>jobs</c> inspection and pending-only cancel/recover, plus the "why is this file held?"
/// diagnostic (see <see cref="HoldDiagnosticStore"/> for what it can and cannot report).
/// </summary>
public static class ProcessingJobsEndpoints
{
    public static IEndpointRouteBuilder MapProcessingJobsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingJobsEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/jobs/inspection", handlers.GetInspectionAsync);
        endpoints.MapV1("POST", "/processing/jobs/{job_id}/cancel-pending", handlers.PostCancelPendingAsync);
        endpoints.MapV1("POST", "/processing/jobs/{job_id}/recover-finalize-failed", handlers.PostRecoverFinalizeFailedAsync);
        endpoints.MapV1("GET", "/processing/files/{file_id}/why-held", handlers.GetWhyHeldAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingJobsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingJobsEndpointHandlers
{
    private readonly JobsInspectionStore _jobsInspection;
    private readonly PendingJobCancellation _pendingJobCancellation;
    private readonly ProcessingJobStore _jobs;
    private readonly HoldDiagnosticStore _holdDiagnostic;
    private readonly MediaManagerConnectionService _connections;
    private readonly FileStateStore _files;
    private readonly LibraryStore _libraries;

    public ProcessingJobsEndpointHandlers(
        JobsInspectionStore jobsInspection,
        PendingJobCancellation pendingJobCancellation,
        ProcessingJobStore jobs,
        HoldDiagnosticStore holdDiagnostic,
        MediaManagerConnectionService connections,
        FileStateStore files,
        LibraryStore libraries)
    {
        _jobsInspection = jobsInspection ?? throw new ArgumentNullException(nameof(jobsInspection));
        _pendingJobCancellation = pendingJobCancellation ?? throw new ArgumentNullException(nameof(pendingJobCancellation));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _holdDiagnostic = holdDiagnostic ?? throw new ArgumentNullException(nameof(holdDiagnostic));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    public async Task<ApiResult> GetInspectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var limit = request.Query("limit") is { } rawLimit && FieldRules.TryInt(new WireString(rawLimit), ["query", "limit"], 1, 100, issues, out var parsedLimit) ? (int)parsedLimit : 50;
        var statuses = request.Context.Request.Query["status"].Where(s => s is not null).Select(s => s!).ToList();
        var knownFilesOnly = request.Query("known_files_only") is { } rawKnownFilesOnly &&
            FieldRules.TryBool(new WireString(rawKnownFilesOnly), ["query", "known_files_only"], issues, out var parsedKnownFilesOnly) && parsedKnownFilesOnly;
        issues.ThrowIfAny();

        try
        {
            _jobsInspection.ValidateStatuses(statuses);
        }
        catch (ArgumentException exception)
        {
            throw new ApiException(422, exception.Message);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var (rows, defaultRecentSlice) = await _jobsInspection.ListAsync(uow, limit, statuses.Count > 0 ? statuses : null, knownFilesOnly).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("jobs", new WireArray(rows.Select(r => (WireValue)ProcessingJobWire.Out(r))))
            .Set("default_recent_slice", defaultRecentSlice));
    }

    public async Task<ApiResult> PostCancelPendingAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("job_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        // One transaction, this request's own (#643): the job, the file it was for and the hand-off it belonged to change
        // together or not at all. Nothing here opens a second connection, so the session touch RequireUserAsync may have
        // written cannot hold a lock this handler then waits for.
        var uow = await request.DbAsync().ConfigureAwait(false);
        var result = await _pendingJobCancellation.CancelAsync(uow, id).ConfigureAwait(false);
        if (result.Outcome == JobActionOutcome.NotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "Job not found.");
        }

        if (result.Outcome == JobActionOutcome.WrongStatus)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Only pending jobs can be cancelled (not leased, completed, failed, or already cancelled).");
        }

        if (result.EndedHandoff is { } handoff)
        {
            await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                ActivityEventTypes.ProcessingHandoffCancelled,
                "processing",
                IntakeRules.CancelledInWeirTitle(handoff.SourceKey, handoff.RelativePath, OperatingSystem.IsWindows()),
                IntakeRules.CancelledDetail(
                    handoff.SourceKey, handoff.HandoffId, handoff.RelativePath, handoff.LibraryId, HandoffLedgerRules.CancelledInWeirMessage, "manual"))).ConfigureAwait(false);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("ok", true).Set("job_id", id).Set("status", ProcessingJobStatus.Cancelled));
    }

    public async Task<ApiResult> PostRecoverFinalizeFailedAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("job_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        // Same reason as the cancel-pending handler above: the session touch is the only write in this
        // request's transaction, and it must be committed before the store's own connection asks for the lock.
        await request.CommitAsync().ConfigureAwait(false);
        var outcome = await _jobs.RecoverHandlerOkFinalizeFailedToCompletedAsync(id, user.User.Username).ConfigureAwait(false);
        if (outcome == JobActionOutcome.NotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "Job not found.");
        }

        if (outcome == JobActionOutcome.WrongStatus)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Only a job whose media work completed but finalization failed can be recovered.");
        }

        return ApiRoutes.Ok(new WireObject().Set("ok", true).Set("job_id", id).Set("status", ProcessingJobStatus.Completed));
    }

    public async Task<ApiResult> GetWhyHeldAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var file = await _files.GetAsync(uow, id).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Weir has no record of that file.");
        var library = await _libraries.GetAsync(uow, file.LibraryId).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "The workflow this file belonged to no longer exists.");

        var outcome = await _holdDiagnostic.EvaluateAsync(uow, file, library, _connections, request.Context.RequestAborted).ConfigureAwait(false);
        var verdict = outcome.Verdict switch
        {
            CandidateGateVerdict.Proceed => "proceed",
            CandidateGateVerdict.WaitUpstream => "wait_upstream",
            CandidateGateVerdict.NotHeld => "not_held",
            _ => "no_upstream_signal",
        };
        return ApiRoutes.Ok(new WireObject()
            .Set("file_id", id)
            .Set("relative_path", file.RelativePath)
            .Set("library_name", library.Name)
            .Set("recorded_status", file.Status)
            .Set("recorded_reason", file.StatusReason)
            .Set("verdict", verdict)
            .Set("owned", outcome.Owned)
            .Set("blocked_upstream", outcome.BlockedUpstream)
            .Set("blocked_by_connection", outcome.BlockedByConnection)
            .Set("queue_row_count", outcome.QueueRowCount)
            .Set("managers_consulted", outcome.ManagersConsulted)
            .Set("managers_reporting", outcome.ManagersReporting)
            .Set("managers_without_queue_signal", new WireArray(outcome.ManagersWithoutQueueSignal.Select(m => (WireValue)WireValue.Of(m))))
            .Set("reasons", new WireArray(outcome.Reasons.Select(r => (WireValue)WireValue.Of(r)))));
    }
}
