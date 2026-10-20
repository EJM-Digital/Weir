using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// A report whose output paths wait for the manager to list its libraries (<see cref="Unplaced"/>). A manager that would not
/// say where it sees the output is usually one that is restarting, and the report it is owed would reach it just as unwell, so the report
/// is held until the paths can be written the way the manager reads them rather than sent with Weir's own.
/// </summary>
public sealed partial class HandoffCompletionReporter
{
    /// <summary>Records the outcome a report states on the hand-off's ledger row, with the output paths it names.</summary>
    private Task RecordOutcomeOfAsync(UnitOfWork uow, string sourceKey, string? handoffId, string state, WireObject body) =>
        _ledger.RecordOutcomeAsync(
            uow,
            sourceKey,
            handoffId,
            state,
            body.Get("outputPath") is WireString output ? output.Value : null,
            body.Get("message") is WireString message ? message.Value : null,
            body.Get("outputFiles") is WireArray files ? [.. files.Items.OfType<WireString>().Select(file => file.Value)] : null);

    /// <summary>
    /// The owed report with its output paths written as the manager sees them, saved on the hand-off and its outcome recorded in the
    /// ledger; null while the manager still will not list its libraries, when the report stays owed as it is. Commits when it places.
    /// </summary>
    private async Task<PendingReport?> PlaceOwedReportAsync(
        UnitOfWork uow, HandoffOrigin origin, HandoffReportTarget target, PendingReport pending, CancellationToken cancellationToken)
    {
        if (pending.Unplaced is not { } unplaced)
        {
            return pending;
        }

        var folder = await ManagerOutputFolderAsync(target.Connection, origin, cancellationToken).ConfigureAwait(false);
        if (!folder.Readable)
        {
            return null;
        }

        var placed = pending with { Body = WithManagerPaths(pending.Body, unplaced.LocalFolder, folder.Path), Unplaced = null };
        await RecordOutcomeOfAsync(uow, origin.SourceKey, origin.HandoffId, unplaced.State, placed.Body).ConfigureAwait(false);
        await HandoffLedgerStore.SetPendingReportAsync(uow, origin.SourceKey, origin.HandoffId!, placed.ToJson()).ConfigureAwait(false);
        await uow.CommitAsync().ConfigureAwait(false);
        return placed;
    }

    /// <summary><paramref name="body"/> with each output path under <paramref name="localFolder"/> written under <paramref name="managerFolder"/>, when the manager has one.</summary>
    private static WireObject WithManagerPaths(WireObject body, string localFolder, string? managerFolder)
    {
        string AsManagerSees(string local) =>
            managerFolder is not null && TranslateOutputPath(local, localFolder, managerFolder) is { } translated ? translated : local;

        var placed = body.Copy();
        if (body.Get("outputPath") is WireString output)
        {
            placed.Set("outputPath", AsManagerSees(output.Value));
        }

        if (body.Get("outputFiles") is WireArray files)
        {
            placed.Set("outputFiles", HandoffOutputFiles.ToJson(files.Items.OfType<WireString>().Select(file => AsManagerSees(file.Value))));
        }

        return placed;
    }
}
