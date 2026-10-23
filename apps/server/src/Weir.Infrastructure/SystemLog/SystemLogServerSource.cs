using System.Globalization;
using Microsoft.Extensions.Logging;
using Weir.Core.Logs;
using Weir.Core.Time;
using Weir.Infrastructure.Logging;

namespace Weir.Infrastructure.SystemLog;

/// <summary>
/// The lines of the server log file in System › Logs. The file is read once through, tallying every line that passes
/// the filters and keeping only the first page of them in the order asked for, so the work grows with the file but the memory does not.
/// </summary>
internal sealed class SystemLogServerSource
{
    private const string UtcMarker = "Z";
    private const string UtcOffset = "+00:00";

    private readonly WeirLogFile _file;

    public SystemLogServerSource(WeirLogFile file) => _file = file ?? throw new ArgumentNullException(nameof(file));

    public Task<SystemLogSlice> ReadAsync(SystemLogRequest request, ILogger logger)
    {
        var filter = request.Filter;
        return filter.CanMatch(SystemLogSource.Server) && File.Exists(_file.Path)
            ? Task.Run(() => Scan(request, logger))
            : Task.FromResult(SystemLogSlice.None);
    }

    private SystemLogSlice Scan(SystemLogRequest request, ILogger logger)
    {
        var filter = request.Filter;
        var selected = filter.Selects(SystemLogSource.Server);
        var text = filter.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        var jobId = filter.JobId?.ToString(CultureInfo.InvariantCulture);
        var matching = 0L;
        var levels = new Dictionary<string, long>();
        var categories = new Dictionary<string, long>();
        var order = request.Order;
        var first = new SortedSet<KeyedRow>(Comparer<KeyedRow>.Create((left, right) => order.Compare(left.Key, right.Key)));
        var lineNumber = 0L;

        void Visit(string rawLine)
        {
            lineNumber++;
            if (SuiteLogFilter.Parse(rawLine) is not { } entry || SuiteLogFilter.SkipLowValueNoise(entry) || RowOf(entry, lineNumber) is not { } row)
            {
                return;
            }

            if (!PassesOtherFilters(filter, entry, row, text, jobId))
            {
                return;
            }

            var levelOk = filter.Levels.Count == 0 || filter.Levels.Contains(row.Level);
            var categoryOk = filter.Categories.Count == 0 || filter.Categories.Contains(row.Category);
            if (categoryOk)
            {
                Increment(levels, row.Level);
            }

            if (levelOk)
            {
                Increment(categories, row.Category);
            }

            if (!levelOk || !categoryOk)
            {
                return;
            }

            matching++;
            if (!selected)
            {
                return;
            }

            var key = order.KeyOf(row, SystemLogOrder.NoWorkflowNames);
            if (request.After is not { } after || order.Compare(key, after) > 0)
            {
                KeepFirst(first, new KeyedRow(row, key), request.Take);
            }
        }

        if (!_file.ReadLines(Visit))
        {
            logger.LogWarning("The server log could not be opened to list it in System › Logs.");
        }

        return selected
            ? new SystemLogSlice([.. first.Select(keyed => keyed.Row)], matching, levels, categories)
            : SystemLogSlice.None with { Matching = matching };
    }

    private static bool PassesOtherFilters(SystemLogFilter filter, ParsedLogEntry entry, SystemLogRow row, string loweredText, string? jobId) =>
        (filter.From is not { } from || row.At >= from)
        && (filter.To is not { } to || row.At <= to)
        && (jobId is null || entry.JobId == jobId)
        && (filter.HasException is not { } wanted || wanted == !string.IsNullOrEmpty(entry.Traceback))
        && (loweredText.Length == 0 || SuiteLogFilter.ContainsText(entry, loweredText));

    private static void KeepFirst(SortedSet<KeyedRow> first, KeyedRow row, int capacity)
    {
        first.Add(row);
        if (first.Count > capacity)
        {
            first.Remove(first.Max!);
        }
    }

    private static void Increment(Dictionary<string, long> tally, string key) =>
        tally[key] = tally.GetValueOrDefault(key) + 1;

    /// <summary>
    /// The line as a row, or null when its time cannot be read, as the Logs list has always left such a line out. An exception stays in
    /// the record's <c>traceback</c>: it is what a person opens the row for, never the line the row reads as.
    /// </summary>
    private static SystemLogRow? RowOf(ParsedLogEntry entry, long lineNumber)
    {
        if (!Timestamp.TryFromIsoFormat(entry.Timestamp.Replace(UtcMarker, UtcOffset, StringComparison.Ordinal), out var at))
        {
            return null;
        }

        return new SystemLogRow(
            SystemLogSource.Server,
            lineNumber,
            new DateTimeOffset(at.AsUtc, TimeSpan.Zero),
            SystemLogRules.ServerLevel(entry.Level),
            SystemLogRules.ServerCategory(entry.Logger),
            null,
            entry.Message,
            entry.Detail,
            SuiteLogFilter.ToOut(entry)!);
    }

    private sealed record KeyedRow(SystemLogRow Row, IReadOnlyList<object?> Key);
}
