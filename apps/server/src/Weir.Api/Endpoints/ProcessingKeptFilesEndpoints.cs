using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Validation;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// "Kept files" (#786 review of #785): every file a person chose to keep without processing again, and the one way
/// back — "Process again" clears the marker and queues the library's watched folder to be looked at, so the file is
/// picked up the same way it would be if it had just appeared.
/// </summary>
public static class ProcessingKeptFilesEndpoints
{
    public static IEndpointRouteBuilder MapProcessingKeptFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingKeptFilesEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/kept-files", handlers.GetKeptFilesAsync);
        endpoints.MapV1("POST", "/processing/kept-files/{id}/process-again", handlers.PostProcessAgainAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingKeptFilesEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingKeptFilesEndpointHandlers
{
    private readonly FileSkipMarkerStore _skipMarkers;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;
    private readonly ArtworkPosterUrls _posters;

    public ProcessingKeptFilesEndpointHandlers(FileSkipMarkerStore skipMarkers, ProcessingJobStore jobs, LibraryStore libraries, ArtworkPosterUrls posters)
    {
        _skipMarkers = skipMarkers;
        _jobs = jobs;
        _libraries = libraries;
        _posters = posters;
    }

    public async Task<ApiResult> GetKeptFilesAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _skipMarkers.ListAsync(uow).ConfigureAwait(false);
        var posters = await _posters.ForFilesAsync(uow, rows.Select(row => (row.LibraryId, row.RelativePath))).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("files", new WireArray(rows.Select(row => (WireValue)new WireObject()
            .Set("id", row.Id)
            .Set("library_id", row.LibraryId)
            .Set("library_name", row.LibraryName)
            .Set("relative_path", row.RelativePath)
            .Set("poster_url", posters.GetValueOrDefault((row.LibraryId, row.RelativePath)))
            .Set("size_bytes", row.SizeBytes)
            .Set("kept_at", row.CreatedAt.ToWireText())))));
    }

    public async Task<ApiResult> PostProcessAgainAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var marker = await _skipMarkers.FindByIdAsync(uow, id).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Weir has no kept file with that id. It may already have been processed again.");
        await _skipMarkers.ClearByIdAsync(uow, id).ConfigureAwait(false);
        var library = await _libraries.GetAsync(uow, marker.LibraryId).ConfigureAwait(false);
        var links = library is null ? WorkflowManagerLinks.None : await _libraries.ManagerLinksAsync(uow, library.Id).ConfigureAwait(false);

        // Committed before EnqueueScanDispatchJobAsync, or the request deadlocks: that call writes through the job
        // store's own connection, which BEGIN IMMEDIATEs while this uow's write above still holds the lock (the
        // same reason RequeueStore.RequeueFileAsync commits before its own enqueue call).
        await request.CommitAsync().ConfigureAwait(false);

        var detail = "Weir will look at this file the next time it scans its workflow.";
        if (links.HandedOffByManager)
        {
            detail = $"{links.ScanSkippedReason} Weir processes this file when Deluno hands it over.";
        }
        else if (library is not null)
        {
            await ProcessingWatchedFolderScanDispatchEnqueue.EnqueueScanDispatchJobAsync(
                uow, _jobs, enqueueRemuxJobs: true, "manual", library.MediaType, library.Id).ConfigureAwait(false);
            detail = "Weir is checking this file's workflow now and will queue it once it is ready.";
        }

        return ApiRoutes.Ok(new WireObject().Set("detail", detail));
    }
}
