using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Net;

namespace Weir.Api.Http;

/// <summary>Response markers shared by the middleware.</summary>
internal static class ResponseMarkers
{
    /// <summary>
    /// Set when the outermost error handler answers 500. That handler sits outside every other middleware, so
    /// the other middleware check this and add none of their headers to its response.
    /// </summary>
    public const string ServerError = "weir.server_error";

    /// <summary>The address the connection really came from, before forwarded headers are applied.</summary>
    public const string ConnectionPeer = "weir.connection_peer";

    public static bool IsServerError(HttpContext context) => context.Items.ContainsKey(ServerError);
}

/// <summary>
/// The outermost error handler: an unhandled exception is answered with a 500 whose JSON body is
/// <c>{"detail": …, "code": "internal_error"}</c>, so a media manager can read it like every other Weir error. The body says
/// nothing of the failure itself: the exception was already logged, with its stack trace, by the request context.
/// </summary>
public sealed class ServerErrorMiddleware
{
    /// <summary>The <c>code</c> an unhandled failure is answered with.</summary>
    public const string Code = "internal_error";

    /// <summary>What a caller is told: that Weir failed, not how.</summary>
    public const string Detail = "Weir ran into an unexpected problem and could not finish this request. The details are in Weir's log.";

    private readonly RequestDelegate _next;
    private readonly WeirOptions _options;

    public ServerErrorMiddleware(RequestDelegate next, WeirOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The last line of defence: any unhandled failure becomes a JSON 500.
        catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
#pragma warning restore CA1031
        {
            context.Items[ResponseMarkers.ServerError] = true;
            context.Response.Clear();
            context.Response.ContentType = "application/json";
            // SecurityHeadersMiddleware skips a response already marked ResponseMarkers.ServerError, so this
            // 500 would otherwise leave with none of nosniff, the frame and permissions policy, or the rest.
            SecurityHeadersMiddleware.Apply(context, _options.SecurityEnableHsts);
            await ApiResponses.WriteDetailAsync(context, new ApiException(StatusCodes.Status500InternalServerError, Detail) { Code = Code }).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Forwarded headers, honoured only from a peer inside <c>WEIR_TRUSTED_PROXY_IPS</c> (none by default).
/// </summary>
/// <remarks>
/// Loopback is not trusted by default (#528): otherwise any local process could rotate
/// <c>X-Forwarded-For</c> past the login rate limit or claim HTTPS.
/// </remarks>
public static class TrustedForwardedHeaders
{
    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        app.Use((context, next) =>
        {
            context.Items[ResponseMarkers.ConnectionPeer] = ApiRequest.DefaultClientHost(context);
            return next(context);
        });

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = null,
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005 // The legacy list still defaults to loopback and is still consulted.
        forwarded.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
        foreach (var raw in options.TrustedProxyIps)
        {
            if (NetRange.TryParse(raw.Trim(), strict: false, out var network))
            {
                forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(new IPAddress(network.AddressBytes()), network.PrefixLength));
            }
        }

        // With both lists empty ASP.NET trusts every peer, so the middleware only runs when a proxy is configured.
        return forwarded.KnownIPNetworks.Count == 0 ? app : app.UseForwardedHeaders(forwarded);
    }
}

/// <summary>
/// CORS for the origins in <c>WEIR_CORS_ORIGINS</c>, when set. Preflight answers, header lists and the
/// <c>Disallowed CORS …</c> 400 body are kept exactly as existing clients and the contract suite expect.
/// </summary>
public sealed class CorsMiddleware
{
    private static readonly string[] AllowMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    private static readonly string[] AllowHeaders =
        [.. new[] { "Accept", "Accept-Language", "Content-Language", "Content-Type", "X-CSRF-Token", "X-Requested-With" }.Order(StringComparer.Ordinal)];

    private readonly RequestDelegate _next;
    private readonly HashSet<string> _origins;
    private readonly HashSet<string> _allowHeadersLower;

    public CorsMiddleware(RequestDelegate next, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _origins = new HashSet<string>(options.CorsOrigins, StringComparer.Ordinal);
        _allowHeadersLower = new HashSet<string>(AllowHeaders.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var originValues = context.Request.Headers.Origin;
        if (originValues.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var origin = originValues[0]!;
        if (context.Request.Method == HttpMethods.Options && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await PreflightAsync(context, origin).ConfigureAwait(false);
            return;
        }

        context.Response.OnStarting(() =>
        {
            if (!ResponseMarkers.IsServerError(context))
            {
                var headers = context.Response.Headers;
                headers["Access-Control-Allow-Credentials"] = "true";
                if (_origins.Contains(origin))
                {
                    headers["Access-Control-Allow-Origin"] = origin;
                    var vary = headers.Vary.ToString();
                    headers.Vary = vary.Length > 0 ? vary + ", Origin" : "Origin";
                }
            }

            return Task.CompletedTask;
        });
        await _next(context).ConfigureAwait(false);
    }

    private Task PreflightAsync(HttpContext context, string origin)
    {
        var headers = context.Response.Headers;
        headers.Vary = "Origin";
        headers["Access-Control-Allow-Methods"] = string.Join(", ", AllowMethods);
        headers["Access-Control-Max-Age"] = "600";
        headers["Access-Control-Allow-Headers"] = string.Join(", ", AllowHeaders);
        headers["Access-Control-Allow-Credentials"] = "true";
        var failures = new List<string>();
        if (_origins.Contains(origin))
        {
            headers["Access-Control-Allow-Origin"] = origin;
        }
        else
        {
            failures.Add("origin");
        }

        var requestedMethod = context.Request.Headers["Access-Control-Request-Method"][0]!;
        if (!AllowMethods.Contains(requestedMethod, StringComparer.Ordinal))
        {
            failures.Add("method");
        }

        var requestedHeaders = context.Request.Headers["Access-Control-Request-Headers"];
        if (requestedHeaders.Count > 0)
        {
            foreach (var header in requestedHeaders[0]!.ToLowerInvariant().Split(','))
            {
                if (!_allowHeadersLower.Contains(header.Trim()))
                {
                    failures.Add("headers");
                    break;
                }
            }
        }

        if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        {
            failures.Add("private-network");
        }

        return failures.Count > 0
            ? ApiResponses.WritePlainTextAsync(context, StatusCodes.Status400BadRequest, "Disallowed CORS " + string.Join(", ", failures))
            : ApiResponses.WritePlainTextAsync(context, StatusCodes.Status200OK, "OK");
    }
}

/// <summary>
/// <c>TrustedProxySchemeMiddleware</c>: only a connection from inside <c>WEIR_TRUSTED_PROXY_IPS</c> may set the
/// scheme, and only with a single <c>http</c> or <c>https</c>.
/// </summary>
public sealed class TrustedProxySchemeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly List<NetRange> _networks;

    public TrustedProxySchemeMiddleware(RequestDelegate next, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _networks = [.. options.TrustedProxyIps
            .Select(value => NetRange.TryParse(value.Trim(), strict: false, out var network) ? network : null)
            .OfType<NetRange>()];
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var peer = context.Items.TryGetValue(ResponseMarkers.ConnectionPeer, out var item) ? item as string : ApiRequest.DefaultClientHost(context);
        if (!string.IsNullOrEmpty(peer) && ClientRateLimitKey.InNetworks(peer, _networks))
        {
            var values = context.Request.Headers["X-Forwarded-Proto"];
            if (values.Count > 0)
            {
                var scheme = values[^1]!.Trim().ToLowerInvariant();
                if (scheme is "http" or "https")
                {
                    context.Request.Scheme = scheme;
                }
            }
        }

        return _next(context);
    }
}

/// <summary><c>HeadMirrorsGetMiddleware</c>: HEAD runs as GET and the body is discarded.</summary>
public sealed class HeadMirrorsGetMiddleware
{
    private readonly RequestDelegate _next;

    public HeadMirrorsGetMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.Method = HttpMethods.Get;
        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        context.Features.Set<IHttpResponseBodyFeature>(new DiscardingBodyFeature(original));
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(original);
            context.Request.Method = HttpMethods.Head;
        }
    }

    private sealed class DiscardingBodyFeature(IHttpResponseBodyFeature inner) : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = Stream.Null;

        public System.IO.Pipelines.PipeWriter Writer { get; } = System.IO.Pipelines.PipeWriter.Create(Stream.Null);

        public void DisableBuffering() => inner.DisableBuffering();

        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            inner.StartAsync(cancellationToken);

        public Task CompleteAsync() => inner.CompleteAsync();
    }
}

/// <summary>CSRF guard: a browser's mutating API call must send <c>X-Requested-With: XMLHttpRequest</c>.</summary>
public sealed class XRequestedWithMiddleware
{
    private static readonly HashSet<string> Mutating = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };
    private readonly RequestDelegate _next;

    public XRequestedWithMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (Mutating.Contains(request.Method) &&
            (request.Path.Value ?? string.Empty).StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            request.Headers.ContainsKey("Origin"))
        {
            var xrw = (request.Headers["X-Requested-With"].FirstOrDefault() ?? string.Empty).Trim();
            if (!string.Equals(xrw, "xmlhttprequest", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponses.WriteJsonAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    new Core.Json.WireObject().Set("detail", "Missing X-Requested-With header. This endpoint requires an authenticated browser session."));
            }
        }

        return _next(context);
    }
}

/// <summary>Counts log records by level for the metrics endpoints.</summary>
public sealed class MetricsLoggerProvider : ILoggerProvider
{
    private readonly Core.Metrics.RuntimeMetricsStore _metrics;
    private readonly LogLevel _minimum;

    public MetricsLoggerProvider(Core.Metrics.RuntimeMetricsStore metrics, LogLevel minimum)
    {
        _metrics = metrics;
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) => new CountingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CountingLogger(MetricsLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider._metrics.RecordLog(Infrastructure.Logging.LogLineFormat.LevelName(logLevel));
            }
        }
    }
}
