using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Api.Endpoints;

/// <summary>System reconciliation: the report and its repair.</summary>
public static class MediaManagerReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapV1("GET", "/system/reconciliation", GetReconciliationAsync);
        endpoints.MapV1("POST", "/system/reconciliation/repair", PostReconciliationRepairAsync);
        return endpoints;
    }

    private static async Task<ApiResult> GetReconciliationAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await ReconciliationService.BuildReportAsync(uow).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs a reconciliation repair. Like every other operator POST it requires the browser origin check and a
    /// session-bound <c>csrf_token</c> (#527), answering 403 for a bad origin and 400
    /// the plain confirmation-token sentence for a missing or wrong token.
    /// </summary>
    private static async Task<ApiResult> PostReconciliationRepairAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var action = model.Str("action", minLength: 1, maxLength: 100);
        var dbId = model.OptionalInt("db_id", ge: 1);
        var path = model.OptionalStr("path", maxLength: 2000);
        var confirm = model.Bool("confirm", defaultValue: false);
        var csrfToken = model.OptionalStr("csrf_token");
        model.Finish(ExtraFields.Ignore);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        WireObject result;
        try
        {
            result = await ReconciliationService.RepairAsync(uow, action, dbId, path, confirm).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        var applied = result["applied"].IsTruthy;
        var message = WireConvert.Str(result["message"]);
        await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.SystemReconciliationRepair,
            "system",
            applied ? "System repair action completed" : "System repair action skipped",
            $"{action}: {message}")).ConfigureAwait(false);
        return ApiRoutes.Ok(result);
    }
}
