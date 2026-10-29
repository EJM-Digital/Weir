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
using Weir.Infrastructure.Http;

namespace Weir.Api.Endpoints;

/// <summary>Getting back in with a recovery code from the sign-in page: the code, then a new password.</summary>
public static class AuthRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapAuthRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<AuthRecoveryEndpointHandlers>();
        endpoints.MapV1("POST", "/auth/recovery/code", handlers.PostCodeAsync);
        endpoints.MapV1("POST", "/auth/recovery/password", handlers.PostPasswordAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="AuthRecoveryEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class AuthRecoveryEndpointHandlers
{
    /// <summary>The one key every address is counted under for the limit on all of them together.</summary>
    private const string AllAddresses = "all addresses";

    private readonly ActivityStore _activity;
    private readonly AuthRateLimiters _limiters;
    private readonly NotificationDispatcher _alerts;

    public AuthRecoveryEndpointHandlers(ActivityStore activity, AuthRateLimiters limiters, NotificationDispatcher alerts)
    {
        _activity = activity;
        _limiters = limiters;
        _alerts = alerts;
    }

    /// <summary>
    /// Spends a recovery code: every session of the account ends, Activity and the alerts hear of it, and the answer carries the
    /// username and the token for choosing a new password. The code stays spent if the password is never chosen.
    /// </summary>
    public async Task<ApiResult> PostCodeAsync(ApiRequest request)
    {
        var body = await request.ReadAnonymousBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var code = model.Str("code", minLength: 1, maxLength: 64);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        if (!_limiters.RecoveryAttempts.Allow(request.RateLimitKey()) || !_limiters.RecoveryAttemptsOverall.Allow(AllAddresses))
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                RecoveryCodeMessages.TooManyTries,
                new Dictionary<string, string> { ["Retry-After"] = RecoveryCodeRules.TryWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: true))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.ConfirmationExpired);
        }

        var logger = AuthEndpoints.Logger(request);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var recovered = await request.Auth.RedeemRecoveryCodeAsync(uow, code).ConfigureAwait(false);
        if (recovered is null)
        {
            logger.LogWarning("auth event: recovery code refused");
            throw new ApiException(StatusCodes.Status400BadRequest, RecoveryCodeMessages.CodeNotRight);
        }

        var user = recovered.User;
        logger.LogInformation("auth event: sign-in recovered with a recovery code (user_id={UserId}, codes_left={CodesLeft})", user.Id, recovered.CodesLeft);
        await _activity.RecordAsync(
            uow, ActivityEventTypes.AuthSignInRecovered, "auth", RecoveryCodeMessages.Reset,
            RecoveryCodeMessages.ActivityDetail(user.Username, recovered.CodesLeft)).ConfigureAwait(false);
        _alerts.DispatchSecurityAlert(
            request.Database,
            RecoveryCodeMessages.AlertTitle,
            RecoveryCodeMessages.Reset,
            (message, exception) => logger.LogWarning(exception, "{Message}", message));
        return new JsonApiResult(
            StatusCodes.Status200OK,
            new WireObject()
                .Set("username", user.Username)
                .Set("recovery_token", request.Auth.IssueRecoveryResetToken(secret, user))
                .Set("codes_left", recovered.CodesLeft))
        {
            Headers = [new("Cache-Control", "no-store, private")],
        };
    }

    /// <summary>Sets the new password for the account a spent code opened, checked with the same rules as any password.</summary>
    public async Task<ApiResult> PostPasswordAsync(ApiRequest request)
    {
        var body = await request.ReadAnonymousBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var token = model.Str("recovery_token", minLength: 1, maxLength: 512);
        var newPassword = model.Str("new_password", minLength: PasswordPolicy.MinPasswordLength, maxLength: 512);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: true))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.ConfirmationExpired);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        string username;
        try
        {
            username = await request.Auth.ResetPasswordAfterRecoveryAsync(uow, secret, token, newPassword).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        _limiters.LoginUsernameBackoff.RecordSuccess(username);
        AuthEndpoints.Logger(request).LogInformation("auth event: password set after a recovery");
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthPasswordChanged, "auth", "Password changed", username).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("message", RecoveryCodeMessages.PasswordSet).Set("username", username));
    }
}
