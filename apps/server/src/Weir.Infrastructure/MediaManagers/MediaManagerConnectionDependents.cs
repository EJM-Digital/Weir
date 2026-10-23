using Weir.Core.Activity;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// What still depends on a media manager connection, and the three ways to let go of it: unlink its workflows, remove them, or give up on
/// the reports Weir still owes it. A connection is removed only once nothing depends on it, so a workflow that was fed by a manager never
/// becomes one that Weir scans for itself by accident, and a report owed to a manager that never comes back cannot hold a connection for ever.
/// </summary>
public sealed class MediaManagerConnectionDependents
{
    private readonly MediaManagerConnectionStore _connections;
    private readonly LibraryStore _libraries;
    private readonly HandoffLedgerStore _ledger;
    private readonly HandoffCompletionReporter _reporter;

    public MediaManagerConnectionDependents(
        MediaManagerConnectionStore connections, LibraryStore libraries, HandoffLedgerStore ledger, HandoffCompletionReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(reporter);
        _connections = connections;
        _libraries = libraries;
        _ledger = ledger;
        _reporter = reporter;
    }

    /// <summary>What stands in the way of removing <paramref name="connection"/>, as it is right now. Call it inside the write that removes the connection.</summary>
    public async Task<ConnectionRemovalBlockers> BlockersAsync(UnitOfWork uow, MediaManagerConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connection);
        var (inProgress, owed) = await _ledger.OpenForConnectionAsync(uow, connection, await OnlyOfItsKindAsync(uow, connection).ConfigureAwait(false)).ConfigureAwait(false);
        var workflows = await _libraries.LibrariesForConnectionIdAsync(uow, connection.Id).ConfigureAwait(false);
        return new ConnectionRemovalBlockers(
            [.. workflows.Select(workflow => new BlockedWorkflow(workflow.Id, workflow.Name))], inProgress, [.. owed.Select(report => report.ToOwedReport())]);
    }

    /// <summary>
    /// Unlinks every workflow from the connection, leaving the workflows, their settings and their folders as they are. One that
    /// no manager feeds any more is switched off, so it does not start scanning its folder until the person has chosen what it does next.
    /// </summary>
    public async Task<IReadOnlyList<BlockedWorkflow>> UnlinkWorkflowsAsync(UnitOfWork uow, MediaManagerConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connection);
        var unlinked = await _libraries.UnlinkConnectionAsync(uow, connection.Id).ConfigureAwait(false);
        return [.. unlinked.Select(workflow => new BlockedWorkflow(workflow.Id, workflow.Name))];
    }

    /// <summary>
    /// Stops owing the connection every report that is still owed, because the person asked. Nothing is sent to the manager. The reports
    /// dropped are recorded in Activity, by hand-off and file, and returned.
    /// </summary>
    public async Task<IReadOnlyList<OwedReport>> GiveUpOwedReportsAsync(UnitOfWork uow, MediaManagerConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(connection);
        var owed = await _ledger.OwedReportsAsync(uow, connection, await OnlyOfItsKindAsync(uow, connection).ConfigureAwait(false)).ConfigureAwait(false);
        if (owed.Count == 0)
        {
            return [];
        }

        await _reporter.GiveUpOwedReportsAsync(uow, connection.Label, owed).ConfigureAwait(false);
        var dropped = owed.Select(report => report.ToOwedReport()).ToList();
        await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingHandoffReportsDropped,
            "processing",
            GivenUpReports.Title(connection.Label, dropped.Count),
            GivenUpReports.Detail(connection.Label, dropped))).ConfigureAwait(false);
        return dropped;
    }

    private async Task<bool> OnlyOfItsKindAsync(UnitOfWork uow, MediaManagerConnectionRecord connection) =>
        (await _connections.ListAsync(uow).ConfigureAwait(false)).All(other => other.Id == connection.Id || other.Kind != connection.Kind);
}
