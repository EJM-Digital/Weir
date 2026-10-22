using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Logs;
using Weir.Core.Metrics;
using Weir.Core.Validation;
using Weir.Infrastructure.Browse;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Media;

namespace Weir.Api.Endpoints;

/// <summary>The local directory browser, the media-tools report, the suite log viewer and runtime metrics.</summary>
public static class SuiteDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapSuiteDiagnosticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteDiagnosticsEndpointHandlers>();

        // Local directory browser.
        endpoints.MapV1("GET", "/system/directories", handlers.GetDirectoriesAsync);

        // #548: the external media tools this install actually has.
        endpoints.MapV1("GET", "/system/media-tools", handlers.GetMediaToolsAsync);

        endpoints.MapV1("GET", "/suite/logs", handlers.GetLogsAsync);
        endpoints.MapV1("GET", "/suite/metrics", handlers.GetMetricsAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteDiagnosticsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class SuiteDiagnosticsEndpointHandlers
{
    private readonly MediaTools _mediaTools;
    private readonly WeirLogFile _logFile;
    private readonly RuntimeMetricsStore _metrics;

    public SuiteDiagnosticsEndpointHandlers(MediaTools mediaTools, WeirLogFile logFile, RuntimeMetricsStore metrics)
    {
        _mediaTools = mediaTools;
        _logFile = logFile;
        _metrics = metrics;
    }

    /// <summary>
    /// #548: which external media tools this install has, and what they say they are. Weir bundles both in the
    /// Windows package (<c>server\bin\ffmpeg</c>, <c>server\bin\mkvtoolnix</c>) and the Docker image, but a
    /// source install provides its own. This lets an operator see whether mkvmerge, the one that matters most for a
    /// library's writer setting, was found: the writer defaults to "best", and "best" silently means ffmpeg wherever
    /// mkvmerge is missing, so without this an install with no mkvmerge looks identical to one that uses it.
    ///
    /// <para>
    /// Always 200. <c>mkvmerge: "not installed"</c> is the correct answer for an install without it, not a
    /// failure — the writer falls back to ffmpeg and everything still works, which is the whole reason
    /// <see cref="IMediaToolResolver.ResolveMkvmerge"/> returns null instead of throwing. A missing ffmpeg
    /// reports the same way rather than erroring, because an operator diagnosing exactly that needs the answer,
    /// not a 500.
    /// </para>
    /// </summary>
    public async Task<ApiResult> GetMediaToolsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var report = await _mediaTools
            .DescribeVersionsAsync(request.Context.RequestAborted)
            .ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("ffmpeg", report.Ffmpeg)
            .Set("mkvmerge", report.Mkvmerge));
    }

    public async Task<ApiResult> GetDirectoriesAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        try
        {
            return ApiRoutes.Ok(DirectoryBrowser.Browse(request.Query("path")));
        }
        catch (DirectoryBrowseException exception)
        {
            throw new ApiException(exception.StatusCode, exception.Message);
        }
    }

    public async Task<ApiResult> GetLogsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var level = request.Query("level");
        var search = request.Query("search");
        bool? hasException = null;
        if (request.Query("has_exception") is { } rawHasException && FieldRules.TryBool(new WireString(rawHasException), ["query", "has_exception"], issues, out var parsedHasException))
        {
            hasException = parsedHasException;
        }

        long limit = 100;
        if (request.Query("limit") is { } rawLimit && FieldRules.TryInt(new WireString(rawLimit), ["query", "limit"], null, null, issues, out var parsedLimit))
        {
            limit = parsedLimit > long.MaxValue ? long.MaxValue : parsedLimit < long.MinValue ? long.MinValue : (long)parsedLimit;
        }

        issues.ThrowIfAny();
        var filter = new SuiteLogFilter(level, search, hasException, limit);
        var result = SuiteLogFilter.Empty;
        if (File.Exists(_logFile.Path))
        {
            if (_logFile.ReadLines(filter.Add))
            {
                result = filter.Result();
            }
            else
            {
                request.LoggerFactory.CreateLogger("weir.platform.suite_settings.logs_service").LogWarning("Suite log read skipped because the active log could not be opened.");
            }
        }

        var items = new List<WireValue>();
        var logger = request.LoggerFactory.CreateLogger("weir.platform.suite_settings.router");
        foreach (var entry in result.Items)
        {
            var output = SuiteLogFilter.ToOut(entry);
            if (output is null)
            {
                logger.LogWarning("Skipping suite log entry with invalid timestamp: {Timestamp}", entry.Timestamp);
                continue;
            }

            items.Add(output);
        }

        return ApiRoutes.Ok(new WireObject()
            .Set("items", new WireArray(items))
            .Set("total", result.Total)
            .Set("counts", new WireObject().Set("error", result.Errors).Set("warning", result.Warnings).Set("information", result.Information)));
    }

    public async Task<ApiResult> GetMetricsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(_metrics.SuiteMetricsOut());
    }
}
