using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Weir.Core.Logs;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.SystemLog;

/// <summary>
/// System › Logs: Weir's events, its jobs and its server log read as one list, newest first. Each source finds its own newest
/// rows and counts what the filters leave, and the merge keeps the newest of them all, so a page costs the same however the
/// rows are spread and the browser never has to put three lists together.
/// </summary>
public sealed class SystemLogReader
{
    /// <summary>The rows a page holds when the request does not say.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The most rows a page holds.</summary>
    public const int MaxLimit = 100;

    /// <summary>The rows an export reads at a time. Each read goes through the whole server log, so an export reads in far larger pages than the list does.</summary>
    public const int ExportPageSize = 1_000;

    private readonly SystemLogEventSource _events = new();
    private readonly SystemLogJobSource _jobs = new();
    private readonly SystemLogServerSource _server;
    private readonly ILogger<SystemLogReader> _logger;

    public SystemLogReader(WeirLogFile logFile, ILogger<SystemLogReader> logger)
    {
        _server = new SystemLogServerSource(logFile);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// One page of the log in <paramref name="order"/>, after the row whose key is <paramref name="after"/> (from the first when
    /// null), and the counts of what the filters leave.
    /// </summary>
    public Task<SystemLogPage> ReadAsync(UnitOfWork uow, SystemLogFilter filter, SystemLogOrder order, IReadOnlyList<object?>? after, int limit) =>
        ReadPageAsync(uow, filter, order, after, Math.Clamp(limit, 1, MaxLimit));

    /// <summary>
    /// The first <paramref name="maxRows"/> rows of the log in <paramref name="order"/>, <see cref="ExportPageSize"/> at a time, so an
    /// export never holds more than one page. The first page is returned even when it is empty, and every page carries the
    /// total the filters leave.
    /// </summary>
    public async IAsyncEnumerable<SystemLogPage> ReadExportPagesAsync(
        UnitOfWork uow,
        SystemLogFilter filter,
        SystemLogOrder order,
        int maxRows,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<object?>? after = null;
        var remaining = maxRows;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadPageAsync(uow, filter, order, after, Math.Clamp(remaining, 1, ExportPageSize)).ConfigureAwait(false);
            yield return page;
            remaining -= page.Rows.Count;
            if (page.NextCursor is null)
            {
                yield break;
            }

            after = order.KeyOfGivenCursor(page.NextCursor);
        }
        while (remaining > 0);
    }

    private async Task<SystemLogPage> ReadPageAsync(UnitOfWork uow, SystemLogFilter filter, SystemLogOrder order, IReadOnlyList<object?>? after, int limit)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(order);
        var request = new SystemLogRequest(filter, order, after, limit + 1);
        var serverRead = _server.ReadAsync(request, _logger);
        var slices = new Dictionary<SystemLogSource, SystemLogSlice>
        {
            [SystemLogSource.Event] = await _events.ReadAsync(uow, request).ConfigureAwait(false),
            [SystemLogSource.Job] = await _jobs.ReadAsync(uow, request).ConfigureAwait(false),
            [SystemLogSource.Server] = await serverRead.ConfigureAwait(false),
        };
        var names = order.Sort == SystemLogSort.Workflow ? await WorkflowNamesAsync(uow, WorkflowIds(slices.Values)).ConfigureAwait(false) : SystemLogOrder.NoWorkflowNames;
        return SystemLogMerge.Page(slices, filter, order, names, limit);
    }

    private static long[] WorkflowIds(IEnumerable<SystemLogSlice> slices) =>
        [.. slices.SelectMany(slice => slice.Rows).Select(row => row.WorkflowId).OfType<long>().Distinct()];

    /// <summary>The names of the workflows with these ids; one that no longer exists has none.</summary>
    public static async Task<IReadOnlyDictionary<long, string>> WorkflowNamesAsync(UnitOfWork uow, IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var conditions = new SystemLogConditions().AddIn("id", "workflow", [.. ids]);
        var rows = await uow.QueryAsync(
            $"SELECT id, name FROM libraries{conditions.WhereText}",
            reader => (Id: reader.GetInt64(0), Name: reader.GetString(1)),
            conditions.Parameters).ConfigureAwait(false);
        return rows.ToDictionary(row => row.Id, row => row.Name);
    }
}
