using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Logging;

namespace Weir.Api.Http;

/// <summary>
/// <c>X-Request-ID</c> on every response and the request id in every log line.
/// </summary>
public sealed class RequestContextMiddleware
{
    public const string HeaderName = "X-Request-ID";

    private const string EventStreamContentType = "text/event-stream";

    /// <summary>The status the metrics count a request under when the client went away before it was answered.</summary>
    private const int ClientClosedRequest = 499;

    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly Core.Metrics.RuntimeMetricsStore _metrics;
    private readonly RouteTable _routes;
    private readonly TimeProvider _time;

    public RequestContextMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, Core.Metrics.RuntimeMetricsStore metrics, RouteTable routes, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _next = next;
        _logger = loggerFactory.CreateLogger("weir.platform.http.request_context");
        _metrics = metrics;
        _routes = routes;
        _time = time;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var incoming = context.Request.Headers[HeaderName].ToString().Trim();
        // Sanitized once, here: this value flows into every log line for the rest of the request (via the
        // scope below), the unhandled-failure log and the echoed response header, so cleaning it at the
        // source is simpler and safer than cleaning it again at each of those uses.
        var requestId = HttpLogSanitizer.Sanitize(incoming.Length > 0 ? incoming : Guid.NewGuid().ToString("N"));
        using var scope = LogContext.BeginRequest(requestId);
        context.Response.OnStarting(() =>
        {
            if (!ResponseMarkers.IsServerError(context))
            {
                context.Response.Headers[HeaderName] = requestId;
            }

            return Task.CompletedTask;
        });
        var started = _time.GetTimestamp();
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsClientGone(exception, context))
        {
            // The browser left (a page change, a closed tab) while Weir was still working: nobody is waiting for an
            // answer, and nothing failed, so it is neither an error in the log nor a 500 nobody will read. Its time
            // says nothing about how fast Weir answers, so it stays out of the latency figures.
            LogClientLeft(requestId, context);
            _metrics.RecordStreamRequest(context.Request.Method, RouteLabelFor(context), ClientClosedRequest, _time.GetElapsedTime(started).TotalMilliseconds);
            return;
        }
        catch (Exception exception)
        {
            LogUnhandled(exception, requestId, context, started);
            throw;
        }

        var elapsedMs = _time.GetElapsedTime(started).TotalMilliseconds;
        if (IsEventStream(context))
        {
            _metrics.RecordStreamRequest(context.Request.Method, RouteLabelFor(context), context.Response.StatusCode, elapsedMs);
        }
        else
        {
            _metrics.RecordRequest(context.Request.Method, RouteLabelFor(context), context.Response.StatusCode, elapsedMs);
        }
    }

    private static bool IsEventStream(HttpContext context) =>
        context.Response.ContentType?.StartsWith(EventStreamContentType, StringComparison.OrdinalIgnoreCase) == true;

    private string RouteLabelFor(HttpContext context) => RequestRoute.LabelFor(context, _routes);

    /// <summary>
    /// Whether <paramref name="exception"/> is the connection going away rather than a fault: work that was cancelled, a body
    /// that could no longer be read, or a write after the connection dropped, once the request has been aborted.
    /// </summary>
    private static bool IsClientGone(Exception exception, HttpContext context) =>
        context.RequestAborted.IsCancellationRequested && exception is OperationCanceledException or IOException or BadHttpRequestException;

    private void LogClientLeft(string requestId, HttpContext context) =>
        _logger.LogDebug(
            "Request abandoned by the client request_id={RequestId} method={Method} route={Route}",
            requestId,
            HttpLogSanitizer.Sanitize(context.Request.Method),
            HttpLogSanitizer.Sanitize(RouteLabelFor(context)));

    private void LogUnhandled(Exception exception, string requestId, HttpContext context, long started)
    {
        // requestId is already sanitized (see InvokeAsync). Method and RouteLabelFor are sanitized here,
        // for this text log line only: Kestrel rejects control characters in the method token, so Method
        // is not expected to carry one in practice, but nothing here relies on that holding for every
        // possible host (a test server, a future reverse proxy) — sanitize it anyway, it is a no-op for a
        // clean value. RouteLabelFor is the route template when a route matched (never attacker-controlled)
        // but falls back to the decoded request path on a no-match, which is; sanitizing the whole result
        // covers both without needing to know which case applied. The metrics call below keeps the raw,
        // unsanitized values — they are aggregated into counters and JSON, never written to a text log line,
        // so there is nothing here for CWE-117 to forge, and rewriting them would just add another way for
        // the log and metrics labels for the same request to disagree.
        _logger.LogError(
            exception,
            "Unhandled request failure request_id={RequestId} method={Method} route={Route}",
            requestId,
            HttpLogSanitizer.Sanitize(context.Request.Method),
            HttpLogSanitizer.Sanitize(RouteLabelFor(context)));
        _metrics.RecordRequest(context.Request.Method, RouteLabelFor(context), 500, _time.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>
/// Security headers on every response that passes through it. A header a handler already set wins, so a
/// route can override the default.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>API baseline: no scripts, no frames, no base-tag surprises, no forms to third parties.</summary>
    public const string ApiContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    /// <summary>The bundled web app: first-party only; the app self-hosts its fonts.</summary>
    public const string HtmlContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "font-src 'self' data:; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "base-uri 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'";

    /// <summary>No route uses the camera, microphone, geolocation, payment or USB APIs, so every frame is denied them.</summary>
    private const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    private static readonly HashSet<string> NoStoreExactPaths = new(StringComparer.Ordinal) { "/health", "/ready", "/readiness", "/metrics" };

    private readonly RequestDelegate _next;
    private readonly WeirOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, WeirOptions options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Applied here even for the outermost error handler's 500 (ServerErrorMiddleware calls Apply
        // itself before writing that response): every response leaving Weir carries the same baseline,
        // not just the ones that reached routing.
        context.Response.OnStarting(() =>
        {
            if (!ResponseMarkers.IsServerError(context))
            {
                Apply(context, _options.SecurityEnableHsts);
            }

            return Task.CompletedTask;
        });
        return _next(context);
    }

    internal static void Apply(HttpContext context, bool enableHsts)
    {
        var headers = context.Response.Headers;
        var contentType = (context.Response.ContentType ?? string.Empty).ToLowerInvariant();
        SetDefault(headers, "X-Content-Type-Options", "nosniff");
        SetDefault(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
        SetDefault(
            headers,
            "Content-Security-Policy",
            contentType.StartsWith("text/html", StringComparison.Ordinal) ? HtmlContentSecurityPolicy : ApiContentSecurityPolicy);
        SetDefault(headers, "X-Frame-Options", "DENY");
        SetDefault(headers, "Permissions-Policy", PermissionsPolicy);
        SetDefault(headers, "Cross-Origin-Opener-Policy", "same-origin");
        SetDefault(headers, "Cross-Origin-Resource-Policy", "same-origin");
        headers.Remove("Server");

        // API responses can include filesystem paths, credentials metadata and operator settings.
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/v1", StringComparison.Ordinal) || NoStoreExactPaths.Contains(path))
        {
            SetDefault(headers, "Cache-Control", "no-store, private");
        }

        if (enableHsts)
        {
            SetDefault(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }
    }

    private static void SetDefault(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}
