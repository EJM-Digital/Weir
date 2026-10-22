using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Api.Endpoints;

/// <summary>Manual enqueue of one per-file pass.</summary>
public static class ProcessingRemuxPassEndpoints
{
    public static IEndpointRouteBuilder MapProcessingRemuxPassEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingRemuxPassEndpointHandlers>();
        endpoints.MapV1("POST", "/processing/jobs/file-remux-pass/enqueue", handlers.PostEnqueueAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingRemuxPassEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingRemuxPassEndpointHandlers
{
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;

    public ProcessingRemuxPassEndpointHandlers(ProcessingJobStore jobs, LibraryStore libraries)
    {
        _jobs = jobs;
        _libraries = libraries;
    }

    public async Task<ApiResult> PostEnqueueAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var relativeMediaPath = model.Str("relative_media_path", minLength: 1);
        var mediaScope = model.Literal("media_scope", ["movie", "tv"], "movie");
        var libraryId = model.OptionalInt("library_id", ge: 1);
        var passThrough = model.Bool("pass_through_unchanged", false);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        ProcessingJob job;
        try
        {
            job = await RemuxPassEnqueue.EnqueueManualAsync(uow, _jobs, _libraries, relativeMediaPath, mediaScope, libraryId, passThrough)
                .ConfigureAwait(false);
        }
        catch (RemuxPassEnqueueException exception)
        {
            throw new ApiException(exception.StatusCode, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("ok", true)
            .Set("job_id", job.Id)
            .Set("dedupe_key", job.DedupeKey)
            .Set("job_kind", job.JobKind));
    }
}
