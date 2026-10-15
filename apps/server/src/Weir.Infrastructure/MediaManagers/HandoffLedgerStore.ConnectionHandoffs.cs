using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>A report Weir still owes a manager, as saved on the hand-off it is about.</summary>
public sealed record OwedReportRow(string SourceKey, string HandoffId, string RelativePath, string ReportJson)
{
    /// <summary>The file or release folder the hand-off was about.</summary>
    public OwedReport ToOwedReport() => new(HandoffId, RelativePath.TrimEnd('/').Split('/')[^1]);
}

/// <summary>The hand-offs and owed reports that belong to one connection, which decide whether it can be removed.</summary>
public sealed partial class HandoffLedgerStore
{
    /// <summary>
    /// A hand-off nobody could attribute to a connection at intake belongs to the connection when it is the only one of its
    /// kind, because removing it would leave that hand-off with nobody to report to.
    /// </summary>
    private const string BelongsToConnection = "(connection_id = $connection OR (connection_id IS NULL AND $sole = 1 AND source_key = $kind))";

    private static (string, object?)[] OwnerOf(MediaManagerConnectionRecord connection, bool onlyOfItsKind) =>
        [("$connection", connection.Id), ("$sole", onlyOfItsKind ? 1 : 0), ("$kind", connection.Kind)];

    /// <summary>
    /// How many hand-offs of the connection Weir is still working on (queued, scheduled or working, as the manager would be told
    /// right now), and the reports Weir still owes it (<paramref name="onlyOfItsKind"/>: no other connection has its kind).
    /// </summary>
    public async Task<(int InProgress, List<OwedReportRow> Owed)> OpenForConnectionAsync(UnitOfWork uow, MediaManagerConnectionRecord connection, bool onlyOfItsKind)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connection);
        var owner = OwnerOf(connection, onlyOfItsKind);
        var owed = await OwedReportsAsync(uow, connection, onlyOfItsKind).ConfigureAwait(false);
        var terminal = string.Join(", ", HandoffLedgerRules.TerminalStates.Select(state => $"'{state}'"));
        var unfinished = await uow.QueryAsync(
            $"SELECT {LedgerColumns} FROM media_manager_handoffs WHERE {BelongsToConnection} AND state NOT IN ({terminal}) ORDER BY id",
            ReadLedger,
            owner).ConfigureAwait(false);
        var inProgress = 0;
        foreach (var row in unfinished)
        {
            var status = await CurrentStatusAsync(uow, row).ConfigureAwait(false);
            if (!HandoffLedgerRules.TerminalStates.Contains(status.State))
            {
                inProgress++;
            }
        }

        return (inProgress, owed);
    }

    /// <summary>Every report Weir still owes the connection, oldest hand-off first.</summary>
    public Task<List<OwedReportRow>> OwedReportsAsync(UnitOfWork uow, MediaManagerConnectionRecord connection, bool onlyOfItsKind)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connection);
        return uow.QueryAsync(
            $"SELECT source_key, handoff_id, relative_path, pending_report_json FROM media_manager_handoffs WHERE {BelongsToConnection} AND pending_report_json IS NOT NULL ORDER BY id",
            reader => new OwedReportRow(
                SqliteValues.GetString(reader, 0), SqliteValues.GetString(reader, 1), SqliteValues.GetString(reader, 2), SqliteValues.GetString(reader, 3)),
            OwnerOf(connection, onlyOfItsKind));
    }
}
