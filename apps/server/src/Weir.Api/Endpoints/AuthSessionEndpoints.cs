using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Text;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;

namespace Weir.Api.Endpoints;

/// <summary>The signed-in user's own account and session inventory: who am I, which sessions are active, and revoking them.</summary>
public static class AuthSessionEndpoints
{
    public static IEndpointRouteBuilder MapAuthSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<AuthSessionEndpointHandlers>();
        endpoints.MapV1("GET", "/auth/me", handlers.GetMeAsync);
        endpoints.MapV1("GET", "/auth/session", handlers.GetSessionAsync);
        endpoints.MapV1("GET", "/auth/sessions", handlers.GetSessionsAsync);
        endpoints.MapV1("POST", "/auth/sessions/revoke-others", handlers.PostRevokeOthersAsync);
        endpoints.MapV1("POST", "/auth/sessions/{session_id}/revoke", handlers.PostRevokeSessionAsync);
        endpoints.MapV1("GET", "/auth/admin/ping", handlers.GetAdminPingAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="AuthSessionEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class AuthSessionEndpointHandlers
{
    private readonly ActivityStore _activity;

    public AuthSessionEndpointHandlers(ActivityStore activity)
    {
        _activity = activity;
    }

    public async Task<ApiResult> GetMeAsync(ApiRequest request)
    {
        var current = await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("user", AuthService.UserPublic(current.User)));
    }

    public async Task<ApiResult> GetSessionAsync(ApiRequest request)
    {
        var uow = await request.DbAsync().ConfigureAwait(false);
        var pair = await request.Auth.LoadValidSessionAsync(uow, request.RawSessionToken).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
        return ApiRoutes.Ok(request.Auth.SessionPublic(pair.Session, current: true));
    }

    public async Task<ApiResult> GetSessionsAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var pair = await request.Auth.LoadValidSessionAsync(uow, request.RawSessionToken).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
        // Prefer the row RequireUserAsync already loaded: it may carry this request's last-seen touch.
        var current = user.Session.Id == pair.Session.Id ? user.Session : pair.Session;
        var items = await request.Auth.ListActiveSessionsAsync(uow, user.User.Id, current).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("items", new WireArray(items)));
    }

    public async Task<ApiResult> PostRevokeOthersAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        request.RefuseOversizedBody();
        var headerToken = request.FirstHeader("X-CSRF-Token");
        request.ValidateBrowserPostOrigin();
        var secret = request.RequireSessionSecret();
        if (headerToken is null || !request.VerifyCsrf(secret, headerToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.ConfirmationExpired);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var pair = await request.Auth.LoadValidSessionAsync(uow, request.RawSessionToken).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
        var count = await request.Auth.RevokeOtherUserSessionsAsync(uow, user.User.Id, pair.Session.Id).ConfigureAwait(false);
        await _activity.RecordAsync(
            uow, ActivityEventTypes.AuthSessionsRevoked, "auth", "Other sessions signed out",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("message", $"Signed out {Plural.Of(count, "other session")}.").Set("revoked_count", count));
    }

    public async Task<ApiResult> PostRevokeSessionAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        request.RefuseOversizedBody();
        var headerToken = request.FirstHeader("X-CSRF-Token");
        var issues = new ValidationIssues();
        FieldRules.TryUuid(request.RouteValue("session_id") ?? string.Empty, ["path", "session_id"], issues, out var sessionId);
        issues.ThrowIfAny();

        request.ValidateBrowserPostOrigin();
        var secret = request.RequireSessionSecret();
        if (headerToken is null || !request.VerifyCsrf(secret, headerToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.ConfirmationExpired);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var pair = await request.Auth.LoadValidSessionAsync(uow, request.RawSessionToken).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
        if (sessionId.ToString("N") == pair.Session.Id)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "The current session cannot be revoked here.");
        }

        if (!await request.Auth.RevokeUserSessionAsync(uow, user.User.Id, sessionId).ConfigureAwait(false))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "Session is no longer active.");
        }

        await _activity.RecordAsync(uow, ActivityEventTypes.AuthSessionsRevoked, "auth", "Session signed out", "One other session was revoked.").ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("message", "Session signed out.").Set("revoked_count", 1));
    }

    public async Task<ApiResult> GetAdminPingAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("ok", true));
    }
}
