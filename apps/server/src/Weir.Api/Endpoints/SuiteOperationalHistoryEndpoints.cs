using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;

namespace Weir.Api.Endpoints;

/// <summary>Previewing and resetting the suite's operational history (activity events and processing jobs).</summary>
public static class SuiteOperationalHistoryEndpoints
{
    public static IEndpointRouteBuilder MapSuiteOperationalHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteOperationalHistoryEndpointHandlers>();
        endpoints.MapV1("GET", "/suite/operational-history/preview", handlers.GetOperationalHistoryPreviewAsync);
        endpoints.MapV1("POST", "/suite/operational-history/reset", handlers.PostOperationalHistoryResetAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteOperationalHistoryEndpoints"/>, constructor-injected with the store they need.</summary>
internal sealed class SuiteOperationalHistoryEndpointHandlers
{
    private readonly OperationalHistoryStore _history;

    public SuiteOperationalHistoryEndpointHandlers(OperationalHistoryStore history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async Task<ApiResult> GetOperationalHistoryPreviewAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(HistoryOut("preview", await _history.PreviewAsync(uow).ConfigureAwait(false)));
    }

    public async Task<ApiResult> PostOperationalHistoryResetAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var confirm = model.Str("confirm", minLength: 5, maxLength: 32);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        if (!string.Equals(confirm.Trim().ToUpperInvariant(), "RESET", StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Type RESET to confirm clearing activity history.");
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var result = await _history.ResetAsync(uow).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(HistoryOut("reset", result));
    }

    private static WireObject HistoryOut(string status, OperationalHistoryStore.ResetResult result) => new WireObject()
        .Set("status", status)
        .Set("activity_events_deleted", result.ActivityEventsDeleted)
        .Set("jobs_deleted", result.ProcessingJobsDeleted)
        .Set("total_deleted", result.TotalDeleted);
}
