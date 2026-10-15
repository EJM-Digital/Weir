using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>Manual enqueue for watched-folder remux scan dispatch (<c>jobs</c> only).</summary>
public static class ProcessingWatchedFolderScanDispatchEndpoints
{
    public static IEndpointRouteBuilder MapProcessingWatchedFolderScanDispatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingWatchedFolderScanDispatchEndpointHandlers>();
        endpoints.MapV1("POST", "/processing/jobs/watched-folder-remux-scan-dispatch/enqueue", handlers.PostEnqueueAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingWatchedFolderScanDispatchEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingWatchedFolderScanDispatchEndpointHandlers
{
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;

    public ProcessingWatchedFolderScanDispatchEndpointHandlers(ProcessingJobStore jobs, LibraryStore libraries)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    public async Task<ApiResult> PostEnqueueAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var enqueueRemuxJobs = model.Bool("enqueue_remux_jobs", defaultValue: true);
        var mediaScope = model.Literal("media_scope", ["movie", "tv"], defaultValue: "movie");
        var libraryId = model.OptionalInt("library_id", ge: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var (ok, error) = await ProcessingWatchedFolderScanDispatchEnqueue.ValidatePrerequisitesAsync(
            uow, _libraries, enqueueRemuxJobs, mediaScope, libraryId).ConfigureAwait(false);
        if (!ok)
        {
            if (error == ScanDispatchPrerequisiteError.HandedOffByManager)
            {
                var linkedLibrary = libraryId is { } linkedId ? await _libraries.GetAsync(uow, linkedId).ConfigureAwait(false) : await _libraries.SeededForScopeAsync(uow, mediaScope).ConfigureAwait(false);
                var links = await _libraries.ManagerLinksAsync(uow, linkedLibrary!.Id).ConfigureAwait(false);
                throw new ApiException(StatusCodes.Status400BadRequest, links.ScanSkippedReason);
            }

            if (error == ScanDispatchPrerequisiteError.MissingOutputForLiveRemux)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "enqueue_remux_jobs requires a saved output folder for this media scope.");
            }

            var label = mediaScope == "tv" ? "TV" : "Movies";
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                $"{label} watched folder is not set in saved path settings. This scan reads media files under that folder — configure it first.");
        }

        if (await ProcessingWatchedFolderScanDispatchEnqueue.MissingWatchedFolderMessageAsync(uow, _libraries, mediaScope, libraryId).ConfigureAwait(false) is { } missing)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, missing);
        }

        var job = await ProcessingWatchedFolderScanDispatchEnqueue.EnqueueScanDispatchJobAsync(
            uow, _jobs, enqueueRemuxJobs, "manual", mediaScope, libraryId).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);

        return ApiRoutes.Ok(new WireObject()
            .Set("ok", true)
            .Set("job_id", job.Id)
            .Set("dedupe_key", job.DedupeKey)
            .Set("job_kind", job.JobKind));
    }
}
