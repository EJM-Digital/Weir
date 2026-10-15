using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Notifications;
using Weir.Core.Security;
using Weir.Core.Validation;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Http;

/// <summary>What a handler answers with.</summary>
public abstract record ApiResult;

/// <summary>A JSON body, with extra headers (for example <c>Set-Cookie</c>) in the order given.</summary>
public sealed record JsonApiResult(int StatusCode, WireValue Body) : ApiResult
{
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}

/// <summary>A response the handler writes itself (files, plain text, empty bodies).</summary>
public sealed record CustomApiResult(Func<HttpContext, Task> Write) : ApiResult;

/// <summary>The metrics label of a route: its path below the API prefix, so labels stay stable for existing dashboards.</summary>
public sealed record RouteLabel(string Label);

/// <summary>
/// Every API route in registration order, for path-only matches: a known path with the wrong method gets a 405
/// whose <c>Allow</c> header names the methods of the first matching route, as existing clients expect.
/// </summary>
public sealed class RouteTable
{
    private readonly List<(string Template, TemplateMatcher Matcher, string[] Methods, string Label)> _routes = [];
    private readonly Lock _lock = new();

    public void Add(string template, IReadOnlyList<string> methods, string label)
    {
        ArgumentNullException.ThrowIfNull(methods);
        var matcher = new TemplateMatcher(TemplateParser.Parse(template), new RouteValueDictionary());
        lock (_lock)
        {
            _routes.Add((template, matcher, [.. methods], label));
        }
    }

    /// <summary>The first route whose path matches, whatever its methods.</summary>
    public (string Label, IReadOnlyList<string> Methods)? FirstPathMatch(PathString path)
    {
        lock (_lock)
        {
            foreach (var (_, matcher, methods, label) in _routes)
            {
                if (matcher.TryMatch(path, new RouteValueDictionary()))
                {
                    return (label, methods);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every route's actual mounted path (matching the committed OpenAPI document's path exactly — see
    /// <see cref="ApiRoutes.MapApi"/>) and the HTTP methods mapped to it, so the OpenAPI document builder knows
    /// which documented operations the server answers.
    /// </summary>
    public IReadOnlyList<(string Path, IReadOnlyList<string> Methods)> Snapshot()
    {
        lock (_lock)
        {
            return [.. _routes.Select(route => (route.Template, (IReadOnlyList<string>)route.Methods))];
        }
    }
}

/// <summary>Per-process abuse controls: the sign-in and bootstrap rate limiters.</summary>
public sealed class AuthRateLimiters
{
    private int _forwardedWarned;
    private int _cookieNotSecureWarned;

    public AuthRateLimiters(WeirOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Login = new SlidingWindowLimiter(options.AuthLoginRateMaxAttempts, options.AuthLoginRateWindowSeconds, time);
        Bootstrap = new SlidingWindowLimiter(options.BootstrapRateMaxAttempts, options.BootstrapRateWindowSeconds, time);
        LoginUsernameBackoff = new UsernameLoginBackoff(time);
    }

    public SlidingWindowLimiter Login { get; }

    public SlidingWindowLimiter Bootstrap { get; }

    /// <summary>Per-account backoff on top of <see cref="Login"/>'s per-IP limit.</summary>
    public UsernameLoginBackoff LoginUsernameBackoff { get; }

    /// <summary>True the first time only: the ignored-forwarded-headers warning is logged once per process.</summary>
    public bool ShouldWarnForwardedIgnored() => Interlocked.Exchange(ref _forwardedWarned, 1) == 0;

    /// <summary>True the first time only: the cookie-not-Secure warning is logged once per process.</summary>
    public bool ShouldWarnCookieNotSecure() => Interlocked.Exchange(ref _cookieNotSecureWarned, 1) == 0;
}

/// <summary>
/// One API request: its database work (committed when the handler succeeds), the signed-in user once
/// resolved, and the checks routes share (session secret, CSRF, browser origin, rate limits).
/// </summary>
public sealed class ApiRequest : IAsyncDisposable
{
    private UnitOfWork? _uow;
    private SignedInSession? _user;

    public ApiRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Options = context.RequestServices.GetRequiredService<WeirOptions>();
        Time = context.RequestServices.GetRequiredService<TimeProvider>();
        Auth = context.RequestServices.GetRequiredService<AuthService>();
        Database = context.RequestServices.GetRequiredService<SqliteDatabase>();
        LoggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
    }

    public HttpContext Context { get; }

    public WeirOptions Options { get; }

    public TimeProvider Time { get; }

    public AuthService Auth { get; }

    public SqliteDatabase Database { get; }

    public ILoggerFactory LoggerFactory { get; }

    public async Task<UnitOfWork> DbAsync()
    {
        _uow ??= await UnitOfWork.OpenAsync(Database, Context.RequestAborted).ConfigureAwait(false);
        return _uow;
    }

    public async Task CommitAsync()
    {
        if (_uow is not null)
        {
            await _uow.CommitAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_uow is not null)
        {
            await _uow.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commit and close this request's database work now, for a response that outlives it (the Activity
    /// stream must not hold a connection for its lifetime).
    /// </summary>
    public async Task ReleaseDbAsync()
    {
        if (_uow is not null)
        {
            await _uow.CommitAsync().ConfigureAwait(false);
            await _uow.DisposeAsync().ConfigureAwait(false);
            _uow = null;
        }
    }

    /// <summary>The trimmed session cookie value, <see langword="null"/> when absent or blank.</summary>
    public string? RawSessionToken
    {
        get
        {
            var raw = (CookieHeaders.Get(Context, Options.SessionCookieName) ?? string.Empty).Trim();
            return raw.Length == 0 ? null : raw;
        }
    }

    public string? Header(string name)
    {
        var values = Context.Request.Headers[name];
        return values.Count == 0 ? null : string.Join(", ", values.ToArray());
    }

    /// <summary>The first occurrence of a header, <see langword="null"/> when absent.</summary>
    public string? FirstHeader(string name)
    {
        var values = Context.Request.Headers[name];
        return values.Count == 0 ? null : values[0];
    }

    /// <summary>A query parameter: the last value when repeated, <see langword="null"/> when absent.</summary>
    public string? Query(string name)
    {
        var values = Context.Request.Query[name];
        return values.Count == 0 ? null : values[^1];
    }

    public string? RouteValue(string name) => Context.Request.RouteValues.TryGetValue(name, out var value) ? value as string : null;

    /// <summary>The client address after trusted forwarded headers were applied.</summary>
    public string? ClientHost => DefaultClientHost(Context);

    public static string? DefaultClientHost(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>
    /// The signed-in user (401 when there is no valid session, 403 for an unknown role), then a 403 unless the role is
    /// in <paramref name="allowedRoles"/> when that is given.
    /// </summary>
    public async Task<SignedInSession> RequireUserAsync(IReadOnlySet<string>? allowedRoles = null)
    {
        if (_user is null)
        {
            var uow = await DbAsync().ConfigureAwait(false);
            _user = await Auth.LoadValidSessionAsync(uow, RawSessionToken).ConfigureAwait(false)
                ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
            if (!UserRoles.Valid.Contains(_user.User.Role))
            {
                _user = null;
                throw new ApiException(StatusCodes.Status403Forbidden, "Invalid account role.");
            }
        }

        if (allowedRoles is not null && !allowedRoles.Contains(_user.User.Role))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "Forbidden.");
        }

        return _user;
    }

    /// <summary>The session secret, or a 503 when it is not configured.</summary>
    public string RequireSessionSecret()
    {
        var secret = (Options.SessionSecret ?? string.Empty).Trim();
        return secret.Length > 0
            ? secret
            : throw new ApiException(StatusCodes.Status503ServiceUnavailable, "WEIR_SESSION_SECRET must be set for auth endpoints.");
    }

    /// <summary>
    /// When trusted browser origins are configured, a POST must carry an <c>Origin</c> (or failing that a <c>Referer</c>)
    /// from one of them; otherwise 403.
    /// </summary>
    public void ValidateBrowserPostOrigin()
    {
        var trusted = Options.TrustedBrowserOrigins;
        if (trusted.Count == 0)
        {
            return;
        }

        var normalized = trusted.Select(o => o.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        var origin = (Context.Request.Headers.Origin.FirstOrDefault() ?? string.Empty).Trim();
        if (origin.Length > 0)
        {
            if (!normalized.Contains(origin.TrimEnd('/')))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "Origin not allowed.");
            }

            return;
        }

        var referer = (Context.Request.Headers.Referer.FirstOrDefault() ?? string.Empty).Trim();
        if (referer.Length > 0)
        {
            var parsed = SplitUrl.Parse(referer);
            var baseUrl = $"{parsed.Scheme}://{parsed.Netloc}".TrimEnd('/');
            if (!normalized.Contains(baseUrl))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "Referer not allowed.");
            }

            return;
        }

        throw new ApiException(StatusCodes.Status403Forbidden, "Missing Origin or Referer for browser POST.");
    }

    /// <summary>Verifies a CSRF token against this request's session cookie.</summary>
    public bool VerifyCsrf(string secret, string? token, bool allowAnonymous) =>
        CsrfTokens.Verify(secret, token, RawSessionToken, allowAnonymous, Time);

    /// <summary>The standard settings-route check: origin, secret, then a session-bound token.</summary>
    public void RequireConfirmationToken(string? token, string detail = "Your confirmation token expired. Refresh the page and try again.")
    {
        ValidateBrowserPostOrigin();
        var secret = RequireSessionSecret();
        if (!VerifyCsrf(secret, token, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, detail);
        }
    }

    /// <summary>The rate-limit key for this client; <c>X-Forwarded-For</c> counts only from a trusted proxy.</summary>
    public string RateLimitKey()
    {
        var limiters = Context.RequestServices.GetRequiredService<AuthRateLimiters>();
        return ClientRateLimitKey.Resolve(
            ClientHost,
            Header("X-Forwarded-For"),
            Options.TrustedProxyIps,
            () =>
            {
                if (limiters.ShouldWarnForwardedIgnored())
                {
                    LoggerFactory.CreateLogger("weir.platform.auth.rate_limit").LogWarning(
                        "X-Forwarded-For was present but WEIR_TRUSTED_PROXY_IPS is not configured; " +
                        "rate limiting will use the immediate peer address.");
                }
            });
    }

    /// <summary>
    /// Read and decode the body of a route that needs a signed-in caller. A route checks who is asking first, then the size of
    /// the body, its content type and then the body itself, so this refuses to run before <see cref="RequireUserAsync"/> has:
    /// an anonymous caller, or one who may not use the route, learns nothing about what the route reads.
    /// </summary>
    public Task<WireValue?> ReadBodyAsync() =>
        _user is null
            ? throw new InvalidOperationException("The body of a route that needs a sign-in is read after RequireUserAsync, so a caller who is not allowed in learns nothing about it.")
            : ApiRequestBody.ReadAsync(Context);

    /// <summary>
    /// Read and decode the body of a route that anyone may call, or that its caller proves itself to with something other than
    /// a session (sign-in, first-run setup, a media manager's webhook secret). Size, content type and body are checked in that order.
    /// </summary>
    public Task<WireValue?> ReadAnonymousBodyAsync() => ApiRequestBody.ReadAsync(Context);

    /// <summary>
    /// For a route that reads nothing from its body: a body larger than Weir accepts is still refused with a 413, after the
    /// caller has been let in, like the routes that read theirs.
    /// </summary>
    public void RefuseOversizedBody() => ApiRequestBody.RefuseOversized(Context);

    /// <summary>An <c>int</c> path parameter.</summary>
    public long PathInt(string name, ValidationIssues issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var raw = RouteValue(name) ?? string.Empty;
        return FieldRules.TryInt(new WireString(raw), ["path", name], null, null, issues, out var value)
            ? value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value
            : 0;
    }
}
