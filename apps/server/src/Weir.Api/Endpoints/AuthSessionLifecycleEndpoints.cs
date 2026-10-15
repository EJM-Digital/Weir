using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>Starting and ending a session: CSRF issuance, login, first-admin bootstrap and logout.</summary>
public static class AuthSessionLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapAuthSessionLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<AuthSessionLifecycleEndpointHandlers>();
        endpoints.MapV1("GET", "/auth/csrf", handlers.GetCsrfAsync);
        endpoints.MapV1("POST", "/auth/login", handlers.PostLoginAsync);
        endpoints.MapV1("GET", "/auth/bootstrap/status", handlers.GetBootstrapStatusAsync);
        endpoints.MapV1("POST", "/auth/bootstrap", handlers.PostBootstrapAsync);
        endpoints.MapV1("POST", "/auth/logout", handlers.PostLogoutAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="AuthSessionLifecycleEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class AuthSessionLifecycleEndpointHandlers
{
    private readonly AuthStore _users;
    private readonly ActivityStore _activity;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly AuthRateLimiters _limiters;
    private readonly SetupCodeGate _setupCodes;

    public AuthSessionLifecycleEndpointHandlers(
        AuthStore users, ActivityStore activity, SuiteSettingsStore suiteSettings, AuthRateLimiters limiters, SetupCodeGate setupCodes)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _suiteSettings = suiteSettings ?? throw new ArgumentNullException(nameof(suiteSettings));
        _limiters = limiters ?? throw new ArgumentNullException(nameof(limiters));
        _setupCodes = setupCodes ?? throw new ArgumentNullException(nameof(setupCodes));
    }

    public async Task<ApiResult> GetCsrfAsync(ApiRequest request)
    {
        var secret = request.RequireSessionSecret();
        var raw = request.RawSessionToken;
        if (raw is not null)
        {
            var uow = await request.DbAsync().ConfigureAwait(false);
            if (await request.Auth.LoadValidSessionAsync(uow, raw).ConfigureAwait(false) is null)
            {
                raw = null;
            }
        }

        return ApiRoutes.Ok(new WireObject().Set("csrf_token", CsrfTokens.Issue(secret, raw, request.Time)));
    }

    public async Task<ApiResult> PostLoginAsync(ApiRequest request)
    {
        var body = await request.ReadAnonymousBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var username = model.Str("username", minLength: 1, maxLength: 64);
        var password = model.Str("password", minLength: 1, maxLength: 512);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var trustedDevice = model.Bool("trusted_device", defaultValue: false);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var limiters = _limiters;
        if (!limiters.Login.Allow(request.RateLimitKey()))
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "Too many login attempts from this address. Try again later.",
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, request.Options.AuthLoginRateWindowSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var uname = username.Trim();
        var accountWait = limiters.LoginUsernameBackoff.TimeUntilAllowed(uname);
        if (accountWait > TimeSpan.Zero)
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "Too many sign-in attempts for this account. Wait a few minutes, then try again.",
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, (long)Math.Ceiling(accountWait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!request.VerifyCsrf(secret, csrfToken, allowAnonymous: true))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var result = await request.Auth.LoginAsync(
            uow, uname, password, trustedDevice, SessionRules.ClientLabelFromUserAgent(request.FirstHeader("User-Agent"))).ConfigureAwait(false);
        if (result is null)
        {
            AuthEndpoints.Logger(request).LogWarning("auth event: login failed");
            limiters.LoginUsernameBackoff.RecordFailure(uname);
            // A guess that also happens to name a real account is worth an activity trail entry; a typed
            // password (people sometimes swap the two fields) is not something Weir writes to a log a
            // viewer can read.
            var existingAccount = await _users.FindUserByLowerUsernameAsync(uow, uname.ToLowerInvariant()).ConfigureAwait(false);
            await _activity.MaybeRecordLoginFailedAsync(uow, existingAccount?.Username ?? "an unknown username", request.Auth.Now()).ConfigureAwait(false);
            await request.CommitAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status401Unauthorized, "Invalid username or password.");
        }

        limiters.LoginUsernameBackoff.RecordSuccess(uname);
        var (user, session, rawToken) = result.Value;
        AuthEndpoints.Logger(request).LogInformation("auth event: login succeeded (user_id={UserId})", user.Id);
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthLoginSucceeded, "auth", "Signed in", user.Username).ConfigureAwait(false);
        return SignedIn(request, new WireObject().Set("user", AuthService.UserPublic(user)), session, rawToken);
    }

    /// <summary>A 200 that hands the browser its new session cookie.</summary>
    private JsonApiResult SignedIn(ApiRequest request, WireObject body, UserSessionRecord session, string rawToken)
    {
        WarnIfCookieSecureBlockedByUntranslatedTls(request);
        var cookie = CookieHeaders.SetCookieHeader(
            request.Options.SessionCookieName,
            rawToken,
            maxAge: SessionRules.AbsoluteTimeoutDays(session.IsTrustedDevice, request.Options) * 86400,
            expires: null,
            httpOnly: true,
            secure: SessionRules.ResolveCookieSecure(request.Context.Request.Scheme, request.Options.SessionCookieSecureMode),
            sameSite: SessionRules.SameSiteText(request.Options.SessionCookieSameSite));
        return new JsonApiResult(StatusCodes.Status200OK, body)
        {
            Headers = [new("Set-Cookie", cookie), new("Cache-Control", "no-store, private")],
        };
    }

    /// <summary>
    /// A reverse proxy that terminates TLS but isn't in <c>WEIR_TRUSTED_PROXY_IPS</c> leaves every session
    /// cookie without Secure, silently, for as long as it takes an operator to notice their site isn't really
    /// protected. This logs it once per process, the first time a sign-in shows the tell (an https Origin or
    /// Referer on a request that otherwise reads as plain http).
    /// </summary>
    private void WarnIfCookieSecureBlockedByUntranslatedTls(ApiRequest request)
    {
        var blocked = SessionRules.CookieSecureBlockedByUntranslatedTls(
            request.Options.SessionCookieSecureMode,
            request.Context.Request.Scheme,
            request.FirstHeader("Origin"),
            request.FirstHeader("Referer"),
            request.Options.TrustedProxyIps.Count > 0);
        if (blocked && _limiters.ShouldWarnCookieNotSecure())
        {
            AuthEndpoints.Logger(request).LogWarning(
                "The session cookie could not be marked Secure: this request reads as plain http even though " +
                "its Origin or Referer is https. If Weir sits behind a reverse proxy that terminates TLS, set " +
                "WEIR_TRUSTED_PROXY_IPS to that proxy's address so Weir can tell.");
        }
    }

    public async Task<ApiResult> GetBootstrapStatusAsync(ApiRequest request)
    {
        const string schemaNotReady = "Local SQLite schema is not ready (Weir migrates its database at startup; check the server log and restart it).";
        const string databaseUnavailable = "SQLite database unavailable or cannot be opened (check WEIR_HOME and WEIR_DB_PATH).";
        const string queryFailed =
            "Database query failed while checking bootstrap status. " +
            "See the server log, restart Weir so it can migrate the database, and verify WEIR_HOME / WEIR_DB_PATH.";
        bool allowed;
        try
        {
            var uow = await UnitOfWork.OpenAsync(request.Database, request.Context.RequestAborted).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                allowed = await request.Auth.BootstrapAllowedAsync(uow).ConfigureAwait(false);
            }
        }
        catch (SqliteException exception)
        {
            var message = exception.Message.ToLowerInvariant();
            var operational = exception.SqliteErrorCode is 1 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 13 or 14 or 15 or 16 or 17;
            if (operational)
            {
                throw new ApiException(
                    StatusCodes.Status503ServiceUnavailable,
                    message.Contains("no such table", StringComparison.Ordinal) || message.Contains("no such column", StringComparison.Ordinal)
                        ? schemaNotReady
                        : databaseUnavailable);
            }

            throw new ApiException(StatusCodes.Status503ServiceUnavailable, queryFailed);
        }
#pragma warning disable CA1031 // Any failure reading bootstrap status becomes a 503 with a next step, never a bare 500.
        catch (Exception exception) when (exception is not ApiException and not OperationCanceledException)
#pragma warning restore CA1031
        {
            request.LoggerFactory.CreateLogger("weir.platform.auth.router").LogError(exception, "bootstrap status: unexpected failure");
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "Could not read bootstrap status. Check the server log, restart Weir so it can migrate " +
                "the database, and verify WEIR_HOME / WEIR_DB_PATH.");
        }

        var setupCodes = _setupCodes;
        var requiresSetupCode = allowed && setupCodes.HasCode && !SetupCodeGate.IsLoopback(request.ClientHost);
        return ApiRoutes.Ok(new WireObject()
            .Set("bootstrap_allowed", allowed)
            .Set("reason", allowed ? "no_admin_user" : "admin_already_exists")
            .Set("requires_setup_code", requiresSetupCode));
    }

    public async Task<ApiResult> PostBootstrapAsync(ApiRequest request)
    {
        var body = await request.ReadAnonymousBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var username = model.Str("username", minLength: 1, maxLength: 64);
        var password = model.Str("password", minLength: PasswordPolicy.MinPasswordLength, maxLength: 512);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var setupCode = model.OptionalStr("setup_code", maxLength: 32);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var limiters = _limiters;
        if (!limiters.Bootstrap.Allow(request.RateLimitKey()))
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "Too many bootstrap attempts from this address. Try again later.",
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, request.Options.BootstrapRateWindowSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        if (!CsrfTokens.Verify(secret, csrfToken, null, allowAnonymous: true, request.Time))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var setupCodes = _setupCodes;
        if (setupCodes.HasCode && !SetupCodeGate.IsLoopback(request.ClientHost) && !setupCodes.Validate(setupCode))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "This isn't the device Weir is running on. Enter the setup code from Weir's log or the setup-code file in its data folder.");
        }

        var logger = AuthEndpoints.Logger(request);
        logger.LogInformation("auth event: bootstrap attempted");
        var uow = await request.DbAsync().ConfigureAwait(false);
        await uow.BeginImmediateAsync().ConfigureAwait(false);
        if (!await request.Auth.BootstrapAllowedAsync(uow).ConfigureAwait(false))
        {
            logger.LogWarning("auth event: bootstrap denied (admin already exists)");
            await _activity.MaybeRecordBootstrapDeniedAsync(uow, request.Auth.Now()).ConfigureAwait(false);
            await request.CommitAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status403Forbidden, "Bootstrap is not available: an admin user already exists.");
        }

        UserRecord user;
        try
        {
            user = await request.Auth.CreateInitialAdminAsync(uow, username.Trim(), password).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            logger.LogWarning("auth event: bootstrap failed (weak password)");
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (SqliteException exception) when (SqliteValues.IsIntegrityError(exception))
        {
            logger.LogWarning("auth event: bootstrap failed (username conflict)");
            throw new ApiException(StatusCodes.Status409Conflict, "Username already exists.");
        }

        logger.LogInformation("auth event: bootstrap succeeded (user_id={UserId})", user.Id);
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthBootstrapSucceeded, "auth", "Initial admin created", user.Username).ConfigureAwait(false);
        setupCodes.Clear();
        var suite = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
        await _suiteSettings.UpdateAsync(uow, suite, suite with { SetupWizardState = "pending" }).ConfigureAwait(false);

        // The person who just chose this password is the one at the browser, so asking for it again straight away
        // would add a step and nothing else (#704). A standard session: trusting the device is a sign-in choice.
        var (session, rawToken) = await request.Auth.CreateSessionAsync(
            uow, user, trustedDevice: false, SessionRules.ClientLabelFromUserAgent(request.FirstHeader("User-Agent"))).ConfigureAwait(false);
        await _activity.RecordAsync(uow, ActivityEventTypes.AuthLoginSucceeded, "auth", "Signed in", user.Username).ConfigureAwait(false);
        return SignedIn(
            request,
            new WireObject()
                .Set("message", "Account created. You are signed in.")
                .Set("username", user.Username)
                .Set("user", AuthService.UserPublic(user)),
            session,
            rawToken);
    }

    public async Task<ApiResult> PostLogoutAsync(ApiRequest request)
    {
        var body = await request.ReadAnonymousBodyAsync().ConfigureAwait(false);
        var headerToken = request.FirstHeader("X-CSRF-Token");
        string? bodyToken = null;
        if (body is not null and not WireNull)
        {
            var issues = new ValidationIssues();
            var model = new BodyModel(body, issues);
            bodyToken = model.OptionalStr("csrf_token");
            model.Finish(ExtraFields.Forbid);
            issues.ThrowIfAny();
        }

        var secret = request.RequireSessionSecret();
        request.ValidateBrowserPostOrigin();
        var token = (string.IsNullOrEmpty(headerToken) ? string.IsNullOrEmpty(bodyToken) ? string.Empty : bodyToken : headerToken).Trim();
        if (token.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Missing CSRF token (X-CSRF-Token header or body csrf_token).");
        }

        var rawSession = request.RawSessionToken;
        if (!request.VerifyCsrf(secret, token, allowAnonymous: rawSession is null))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, AuthEndpoints.InvalidCsrf);
        }

        var logger = AuthEndpoints.Logger(request);
        if (rawSession is not null)
        {
            var uow = await request.DbAsync().ConfigureAwait(false);
            var pair = await request.Auth.LoadValidSessionAsync(uow, rawSession).ConfigureAwait(false);
            if (pair is not null)
            {
                await _activity.RecordAsync(uow, ActivityEventTypes.AuthLogout, "auth", "Signed out", pair.User.Username).ConfigureAwait(false);
            }

            if (await request.Auth.LogoutByCookieAsync(uow, rawSession).ConfigureAwait(false))
            {
                logger.LogInformation("auth event: logout (session revoked)");
            }
            else
            {
                logger.LogInformation("auth event: logout (no active session matched cookie)");
            }
        }
        else
        {
            logger.LogInformation("auth event: logout (no session cookie)");
        }

        var cookie = CookieHeaders.SetCookieHeader(
            request.Options.SessionCookieName,
            string.Empty,
            maxAge: 0,
            expires: request.Time.GetUtcNow(),
            httpOnly: true,
            secure: SessionRules.ResolveCookieSecure(request.Context.Request.Scheme, request.Options.SessionCookieSecureMode),
            sameSite: SessionRules.SameSiteText(request.Options.SessionCookieSameSite));
        return new CustomApiResult(context =>
        {
            context.Response.Headers.Append("Set-Cookie", cookie);
            context.Response.Headers.CacheControl = "no-store, private";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
    }
}
