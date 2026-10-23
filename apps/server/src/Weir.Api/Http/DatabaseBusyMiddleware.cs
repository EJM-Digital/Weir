using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Http;

/// <summary>
/// Answers a write that gave up on a busy database with a retryable <c>503 Service Unavailable</c> instead of letting it become a 500
/// (#964). It maps the failure by what it is, a <see cref="SqliteException"/> with SQLite's <c>SQLITE_BUSY</c> or <c>SQLITE_LOCKED</c>
/// code, rather than route by route, so every route that writes answers the same way and a route added later cannot forget to.
/// </summary>
/// <remarks>
/// <para>
/// It sits inside <see cref="RequestContextMiddleware"/> and <see cref="SecurityHeadersMiddleware"/>: the busy answer is an ordinary
/// response that carries the request id and the security headers, is counted under its real status, and is not the "unhandled request
/// failure" that middleware logs for an exception it sees pass.
/// </para>
/// <para>
/// <c>Retry-After</c> is the time the writer really waited, as the write gate measured it (<see cref="DatabaseBusyException.Waited"/>),
/// rounded to whole seconds. A database that held the writer for that long is not likely to be free sooner, and a caller that comes
/// back after the same time rather than at once adds no queue of its own to it. A busy answer SQLite gave from inside a statement
/// carries no measure of its own, and SQLite only gives it once the busy timeout has passed, so the busy timeout is what it waited.
/// The figure is kept between <see cref="MinimumRetryAfterSeconds"/> and <see cref="MaximumRetryAfterSeconds"/>, so a one-second wait
/// still asks for a second (a <c>Retry-After</c> of 0 is a retry at once) and a long setting never tells a caller to go away for minutes.
/// </para>
/// </remarks>
public sealed class DatabaseBusyMiddleware
{
    /// <summary>The <c>code</c> a busy database is answered with.</summary>
    public const string Code = "database_busy";

    public const int MinimumRetryAfterSeconds = 1;
    public const int MaximumRetryAfterSeconds = 60;

    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly RouteTable _routes;
    private readonly WeirOptions _options;

    public DatabaseBusyMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, RouteTable routes, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _next = next;
        _logger = loggerFactory.CreateLogger("weir.platform.http.database_busy");
        _routes = routes;
        _options = options;
    }

    /// <summary>The whole seconds a caller is asked to wait after a write that waited <paramref name="waited"/>.</summary>
    public static int RetryAfterSeconds(TimeSpan waited) =>
        (int)Math.Clamp(Math.Round(waited.TotalSeconds, MidpointRounding.AwayFromZero), MinimumRetryAfterSeconds, MaximumRetryAfterSeconds);

    /// <summary>What a caller is told, in the words of the operator messaging standard: what happened and what to do.</summary>
    public static string Detail(int retryAfterSeconds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Weir is busy with other changes and could not finish this request. Try again in {retryAfterSeconds} {(retryAfterSeconds == 1 ? "second" : "seconds")}.");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (DatabaseBusyException.IsBusy(exception) && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            var waited = exception is DatabaseBusyException busy ? busy.Waited : TimeSpan.FromSeconds(_options.SqliteBusyTimeoutSeconds);
            var retryAfter = RetryAfterSeconds(waited);
            _logger.LogWarning(
                "The database stayed busy: a write waited {WaitedSeconds} seconds for it and gave up, so Weir answered 503 and asked the caller to try again in {RetryAfterSeconds} seconds. request_id={RequestId} method={Method} route={Route}",
                waited.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                retryAfter,
                LogContext.RequestId,
                HttpLogSanitizer.Sanitize(context.Request.Method),
                HttpLogSanitizer.Sanitize(RequestRoute.LabelFor(context, _routes)));

            var headers = new Dictionary<string, string> { ["Retry-After"] = retryAfter.ToString(CultureInfo.InvariantCulture) };
            await ApiResponses.WriteDetailAsync(
                context,
                new ApiException(StatusCodes.Status503ServiceUnavailable, Detail(retryAfter), headers) { Code = Code }).ConfigureAwait(false);
        }
    }
}

/// <summary>The route a request is for, as the log and the metrics name it.</summary>
internal static class RequestRoute
{
    /// <summary>The label of the route the request matched (see <see cref="RouteLabel"/>), else the URL path.</summary>
    public static string LabelFor(HttpContext context, RouteTable routes) =>
        context.GetEndpoint()?.Metadata.GetMetadata<RouteLabel>()?.Label
        ?? routes.FirstPathMatch(context.Request.Path)?.Label
        ?? string.Concat(context.Request.Path.Value);
}
