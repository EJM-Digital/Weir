using System.Numerics;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Logs;
using Weir.Core.Paging;
using Weir.Core.Validation;
using Weir.Infrastructure.SystemLog;

namespace Weir.Api.Endpoints;

/// <summary>What a request for System › Logs asks for: the filters, the order, where to start and how many rows.</summary>
internal sealed record SystemLogQuery(SystemLogFilter Filter, SystemLogOrder Order, IReadOnlyList<object?>? After, int Limit)
{
    private const int TextMaxLength = 200;
    private const int EventTypeMaxLength = 64;
    private const char ListSeparator = ',';

    /// <summary>The query string as a filter. Every problem is reported at once, as the other lists do.</summary>
    public static SystemLogQuery Read(ApiRequest request, ValidationIssues issues)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issues);
        var sources = List(request, "source", SystemLogSources.All.Select(SystemLogSources.NameOf).ToList(), issues);
        var filter = new SystemLogFilter
        {
            Sources = [.. sources.Select(name => SystemLogSources.TryParse(name, out var source) ? source : default)],
            Levels = List(request, "level", SystemLogLevels.All, issues),
            Categories = List(request, "category", SystemLogCategories.All, issues),
            JobStatuses = List(request, "status", [.. SystemLogRules.JobStatusLabels.Keys], issues),
            WorkflowId = Integer(request, "workflow", issues),
            JobId = Integer(request, "job", issues),
            Text = Text(request, "q", TextMaxLength, issues),
            EventType = Text(request, "event_type", EventTypeMaxLength, issues),
            Result = Literal(request, "result", [.. ActivityClassifier.Results], issues),
            Trigger = Literal(request, "trigger", [.. ActivityClassifier.Triggers], issues),
            HasException = Flag(request, "has_exception", issues),
            From = When(request, "from", issues),
            To = When(request, "to", issues),
        };
        var limit = ReadLimit(request, issues);
        var order = ReadOrder(request, issues);
        var after = order is null ? null : Cursor(request, order, issues);
        return new SystemLogQuery(filter, order ?? SystemLogOrder.Newest, after, limit);
    }

    /// <summary>The order asked for, or null when the request names a sort or a direction this log does not have. Newest first when it names neither.</summary>
    private static SystemLogOrder? ReadOrder(ApiRequest request, ValidationIssues issues)
    {
        var sort = SystemLogSort.Time;
        var sortValid = request.Query("sort") is not { } rawSort
            || (FieldRules.TryLiteral(new WireString(rawSort), ["query", "sort"], SystemLogSorts.All, issues, out var parsedSort)
                && SystemLogSorts.TryParse(parsedSort, out sort));
        var direction = SortDirection.Descending;
        var directionValid = request.Query("direction") is not { } rawDirection
            || (FieldRules.TryLiteral(new WireString(rawDirection), ["query", "direction"], SortDirections.All, issues, out var parsedDirection)
                && SortDirections.TryParse(parsedDirection, out direction));
        return sortValid && directionValid ? new SystemLogOrder(sort, direction) : null;
    }

    /// <summary>The values of a parameter that may be repeated, or written once with commas between them, each one of <paramref name="allowed"/>.</summary>
    private static List<string> List(ApiRequest request, string name, IReadOnlyList<string> allowed, ValidationIssues issues)
    {
        var values = new List<string>();
        foreach (var raw in request.Context.Request.Query[name])
        {
            foreach (var part in string.Concat(raw).Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FieldRules.TryLiteral(new WireString(part), ["query", name], allowed, issues, out var value) && !values.Contains(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static string? Literal(ApiRequest request, string name, IReadOnlyList<string> allowed, ValidationIssues issues) =>
        request.Query(name) is { Length: > 0 } raw && FieldRules.TryLiteral(new WireString(raw), ["query", name], allowed, issues, out var value) ? value : null;

    private static string? Text(ApiRequest request, string name, int maxLength, ValidationIssues issues) =>
        request.Query(name)?.Trim() is { Length: > 0 } raw && FieldRules.TryStr(new WireString(raw), ["query", name], 1, maxLength, issues, out var value) ? value : null;

    private static long? Integer(ApiRequest request, string name, ValidationIssues issues) =>
        request.Query(name) is { Length: > 0 } raw && FieldRules.TryInt(new WireString(raw), ["query", name], 1, null, issues, out var value)
            ? (long)BigInteger.Min(value, long.MaxValue)
            : null;

    private static bool? Flag(ApiRequest request, string name, ValidationIssues issues) =>
        request.Query(name) is { Length: > 0 } raw && FieldRules.TryBool(new WireString(raw), ["query", name], issues, out var value) ? value : null;

    private static DateTimeOffset? When(ApiRequest request, string name, ValidationIssues issues)
    {
        if (request.Query(name) is not { Length: > 0 } raw || !FieldRules.TryDateTime(new WireString(raw), ["query", name], issues, out var value))
        {
            return null;
        }

        return new DateTimeOffset(value.GetValueOrDefault().AsUtc, TimeSpan.Zero);
    }

    private static int ReadLimit(ApiRequest request, ValidationIssues issues) =>
        request.Query("limit") is { Length: > 0 } raw && FieldRules.TryInt(new WireString(raw), ["query", "limit"], 1, SystemLogReader.MaxLimit, issues, out var value)
            ? (int)value
            : SystemLogReader.DefaultLimit;

    private static IReadOnlyList<object?>? Cursor(ApiRequest request, SystemLogOrder order, ValidationIssues issues)
    {
        if (request.Query("cursor") is not { Length: > 0 } raw)
        {
            return null;
        }

        if (order.TryDecodeCursor(raw, out var after))
        {
            return after;
        }

        issues.Add(new ValidationIssue("value_error", ["query", "cursor"], "Value error, that is not a position this log gave out for this sort", new WireString(raw)));
        return null;
    }
}
