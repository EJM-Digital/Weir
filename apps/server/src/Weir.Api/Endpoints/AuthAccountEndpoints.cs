using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;

namespace Weir.Api.Endpoints;

/// <summary>Changing the signed-in user's own username, password or app theme.</summary>
public static class AuthAccountEndpoints
{
    public static IEndpointRouteBuilder MapAuthAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<AuthAccountEndpointHandlers>();
        endpoints.MapV1("POST", "/auth/change-username", handlers.PostChangeUsernameAsync);
        endpoints.MapV1("POST", "/auth/change-password", handlers.PostChangePasswordAsync);
        endpoints.MapV1("POST", "/auth/theme", handlers.PostSetThemeAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="AuthAccountEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class AuthAccountEndpointHandlers
{
    private readonly ActivityStore _activity;

    public AuthAccountEndpointHandlers(ActivityStore activity)
    {
        _activity = activity;
    }

    public async Task<ApiResult> PostChangeUsernameAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var currentPassword = model.Str("current_password", minLength: 1);
        var newUsername = model.Str("new_username", minLength: 1, maxLength: 64);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        string changed;
        try
        {
            changed = await request.Auth.ChangeUsernameAsync(uow, user.User.Id, currentPassword, newUsername).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        AuthEndpoints.Logger(request).LogInformation("auth event: username changed (user_id={UserId})", user.User.Id);
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthUsernameChanged, "auth", "Username changed", $"Signed in as {changed}.").ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("message", "Username changed. Use it the next time you sign in.")
            .Set("username", changed));
    }

    public async Task<ApiResult> PostChangePasswordAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var currentPassword = model.Str("current_password", minLength: 1);
        var newPassword = model.Str("new_password", minLength: PasswordPolicy.MinPasswordLength, maxLength: 512);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            await request.Auth.ChangePasswordAsync(uow, user.User.Id, currentPassword, newPassword).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        AuthEndpoints.Logger(request).LogInformation("auth event: password changed (user_id={UserId})", user.User.Id);
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthPasswordChanged, "auth", "Password changed", user.User.Username).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("message", "Password changed. Sign in again with your new password."));
    }

    /// <summary>Saves the signed-in user's own colour theme, so it follows them to another browser or device.</summary>
    public async Task<ApiResult> PostSetThemeAsync(ApiRequest request)
    {
        var user = await request.RequireUserAsync().ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var theme = model.Literal("theme", AppThemes.All);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            await request.Auth.SetAppThemeAsync(uow, user.User.Id, theme).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        return ApiRoutes.Ok(new WireObject().Set("message", "Theme saved.").Set("app_theme", theme));
    }
}
