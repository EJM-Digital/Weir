using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Logs;
using Weir.Core.Validation;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.SystemLog;

namespace Weir.Api.Endpoints;

/// <summary>
/// <c>GET /api/v1/system/log</c>: Weir's events, its jobs and its server log as one list, newest first unless it is asked to
/// <c>sort</c> another way, and <c>GET /api/v1/system/log/export</c>: the same list, as a file, for the filters and order given.
/// </summary>
public static class SystemLogEndpoints
{
    public static IEndpointRouteBuilder MapSystemLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SystemLogEndpointHandlers>();
        endpoints.MapV1("GET", "/system/log", handlers.GetLogAsync);
        endpoints.MapV1("GET", "/system/log/export", handlers.GetExportAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SystemLogEndpoints"/>.</summary>
internal sealed class SystemLogEndpointHandlers
{
    private const string ExportFormatPattern = "^(csv|json)$";

    private readonly SystemLogReader _log;

    public SystemLogEndpointHandlers(SystemLogReader log) => _log = log;

    public async Task<ApiResult> GetLogAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var query = SystemLogQuery.Read(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var page = await _log.ReadAsync(uow, query.Filter, query.Order, query.After, query.Limit).ConfigureAwait(false);
        var names = await WorkflowNamesAsync(uow, page.Rows).ConfigureAwait(false);
        return ApiRoutes.Ok(SystemLogWire.Page(page, names));
    }

    public async Task<ApiResult> GetExportAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var json = ExportAsJson(request, issues);
        var query = SystemLogQuery.Read(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var writer = new SystemLogExportWriter(json);
        var fileName = SystemLogWire.ExportFileName(request.Time.GetLocalNow(), writer.Extension);
        return new CustomApiResult(async context =>
        {
            await using var pages = _log.ReadExportPagesAsync(uow, query.Filter, query.Order, ActivityHistory.ExportMaxRows, context.RequestAborted)
                .GetAsyncEnumerator(context.RequestAborted);
            if (!await pages.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("An export always has a first page, even an empty one.");
            }

            var exportRows = Math.Min(pages.Current.Total, ActivityHistory.ExportMaxRows);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = writer.ContentType;
            context.Response.Headers["X-Weir-Export-Limit"] = ActivityHistory.ExportMaxRows.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-Weir-Export-Rows"] = exportRows.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            await WriteAsync(context, writer.Start()).ConfigureAwait(false);
            do
            {
                var names = await WorkflowNamesAsync(uow, pages.Current.Rows).ConfigureAwait(false);
                await WriteAsync(context, writer.Rows(pages.Current.Rows, names)).ConfigureAwait(false);
            }
            while (await pages.MoveNextAsync().ConfigureAwait(false));
            await WriteAsync(context, writer.End()).ConfigureAwait(false);
        });
    }

    private static Task WriteAsync(HttpContext context, string text) =>
        text.Length == 0 ? Task.CompletedTask : context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestAborted).AsTask();

    private static Task<IReadOnlyDictionary<long, string>> WorkflowNamesAsync(UnitOfWork uow, IReadOnlyList<SystemLogRow> rows) =>
        SystemLogReader.WorkflowNamesAsync(uow, [.. rows.Select(row => row.WorkflowId).OfType<long>().Distinct()]);

    private static bool ExportAsJson(ApiRequest request, ValidationIssues issues)
    {
        var format = request.Query("format");
        if (format is null || format is "csv")
        {
            return false;
        }

        if (format is "json")
        {
            return true;
        }

        issues.Add(new ValidationIssue(
            "string_pattern_mismatch",
            ["query", "format"],
            $"String should match pattern '{ExportFormatPattern}'",
            new WireString(format),
            new WireObject().Set("pattern", ExportFormatPattern)));
        return false;
    }
}
