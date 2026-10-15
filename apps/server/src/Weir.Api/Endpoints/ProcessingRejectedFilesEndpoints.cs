using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// "Process all again" for rejected files: after the rules change, every rejected file whose original is still in its
/// watched folder is put back to work in one step. It covers the whole rejected set, not only what Activity currently
/// lists, narrowed to one workflow when <c>library_id</c> is given.
/// </summary>
public static class ProcessingRejectedFilesEndpoints
{
    public static IEndpointRouteBuilder MapProcessingRejectedFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingRejectedFilesEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/files/rejected/summary", handlers.GetSummaryAsync);
        endpoints.MapV1("POST", "/processing/files/rejected/process-again", handlers.PostProcessAgainAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingRejectedFilesEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingRejectedFilesEndpointHandlers
{
    private readonly RejectedFilesAgain _rejected;

    public ProcessingRejectedFilesEndpointHandlers(FileStateStore files, ProcessingJobStore jobs, LibraryStore libraries)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(libraries);
        _rejected = new RejectedFilesAgain(files, new RequeueStore(jobs, libraries));
    }

    public async Task<ApiResult> GetSummaryAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        long? libraryId = request.Query("library_id") is { } rawLibrary
            && FieldRules.TryInt(new WireString(rawLibrary), ["query", "library_id"], 1, null, issues, out var parsedLibrary)
            ? (long)parsedLibrary
            : null;
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var summary = await _rejected.SummarizeAsync(uow, libraryId).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("rejected", summary.Rejected).Set("ready", summary.Ready));
    }

    public async Task<ApiResult> PostProcessAgainAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var libraryId = model.OptionalInt("library_id", ge: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var result = await _rejected.ProcessAgainAsync(uow, libraryId).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("requeued", result.Requeued).Set("skipped", result.Skipped).Set("detail", result.Detail));
    }
}
