using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Auth;

namespace Weir.Api.Endpoints;

/// <summary>The signed-in user's recovery codes: how many are left, a new set, and saying the set was saved.</summary>
public static class AuthRecoveryCodesEndpoints
{
    public static IEndpointRouteBuilder MapAuthRecoveryCodesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<AuthRecoveryCodesEndpointHandlers>();
        endpoints.MapV1("GET", "/auth/recovery-codes", handlers.GetStatusAsync);
        endpoints.MapV1("POST", "/auth/recovery-codes", handlers.PostNewSetAsync);
        endpoints.MapV1("POST", "/auth/recovery-codes/confirm", handlers.PostConfirmAsync);
        return endpoints;
    }

    /// <summary>How many codes are left and whether the set was saved, as the API shows them.</summary>
    internal static WireObject StatusOut(RecoveryCodeStatus status) =>
        new WireObject().Set("remaining", status.Remaining).Set("confirmed", status.Confirmed);
}

/// <summary>Handlers for <see cref="AuthRecoveryCodesEndpoints"/>.</summary>
internal sealed class AuthRecoveryCodesEndpointHandlers
{
    public async Task<ApiResult> GetStatusAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var status = await request.Auth.RecoveryCodeStatusAsync(uow, user.User.Id).ConfigureAwait(false);
        return ApiRoutes.Ok(AuthRecoveryCodesEndpoints.StatusOut(status));
    }

    /// <summary>Makes a new set after the current password, voiding the old one, and shows it once.</summary>
    public async Task<ApiResult> PostNewSetAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var currentPassword = model.Str("current_password", minLength: 1);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();
        request.RequireConfirmationToken(csrfToken);

        await request.RequireCurrentPasswordAsync(currentPassword).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var codes = await request.Auth.MakeRecoveryCodesAsync(uow, user.User.Id).ConfigureAwait(false);

        AuthEndpoints.Logger(request).LogInformation("auth event: new recovery codes made (user_id={UserId})", user.User.Id);
        return new JsonApiResult(
            StatusCodes.Status200OK,
            new WireObject()
                .Set("message", RecoveryCodeMessages.NewSetMade)
                .Set("codes", new WireArray(codes.Select(code => (WireValue)new WireString(code))))
                .Set("remaining", codes.Count)
                .Set("confirmed", false))
        {
            Headers = [new("Cache-Control", "no-store, private")],
        };
    }

    public async Task<ApiResult> PostConfirmAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();
        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        await request.Auth.ConfirmRecoveryCodesAsync(uow, user.User.Id).ConfigureAwait(false);
        var status = await request.Auth.RecoveryCodeStatusAsync(uow, user.User.Id).ConfigureAwait(false);
        return ApiRoutes.Ok(AuthRecoveryCodesEndpoints.StatusOut(status).Set("message", RecoveryCodeMessages.SetConfirmed));
    }
}
