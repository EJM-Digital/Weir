using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>What startup runs to give the hand-offs left waiting with nothing to run a fresh pass.</summary>
public interface IStuckHandoffRepair
{
    /// <summary>Gives every stuck hand-off a pass; returns how many it repaired.</summary>
    Task<int> RepairAsync(CancellationToken cancellationToken);
}

/// <summary>
/// At startup, gives a hand-off that is still waiting but has no work left to run a fresh pass, the way a send of it again would.
/// A hand-off can be left like that when its last job ended without the hand-off reaching a final state; nothing would ever
/// run it, every status read would say "queued", and the manager would never send it again.
/// </summary>
/// <remarks>
/// A hand-off with a job waiting or running, one waiting out a retry, and one that has ended are left alone, so running this
/// again while the fresh pass is still waiting queues nothing more.
/// </remarks>
public sealed class StuckHandoffRepair : IStuckHandoffRepair
{
    private readonly SqliteDatabase _database;
    private readonly HandoffLedgerStore _ledger;
    private readonly MediaManagerIntake _intake;
    private readonly ILogger<StuckHandoffRepair> _logger;

    public StuckHandoffRepair(SqliteDatabase database, HandoffLedgerStore ledger, MediaManagerIntake intake, ILogger<StuckHandoffRepair> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> RepairAsync(CancellationToken cancellationToken)
    {
        var stuck = await StuckAsync(cancellationToken).ConfigureAwait(false);
        var repaired = 0;
        foreach (var row in stuck)
        {
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var libraries = await MediaManagerIntake.ListLibrariesAsync(uow).ConfigureAwait(false);
                if (libraries.FirstOrDefault(library => library.Id == row.LibraryId) is not { } library)
                {
                    continue;
                }

                try
                {
                    await _intake.EnqueueRefineAsync(uow, await ResendOfAsync(uow, row, library).ConfigureAwait(false), row.ConnectionId).ConfigureAwait(false);
                }
                catch (IntakeRefusedException exception)
                {
                    _logger.LogWarning(
                        "{Release} was waiting for Weir to start it but Weir could not give it a fresh pass: {Reason}",
                        row.RelativePath.TrimEnd('/').Split('/')[^1],
                        exception.Message);
                    continue;
                }

                await uow.CommitAsync().ConfigureAwait(false);
            }

            repaired++;
            _logger.LogInformation(
                "{Release} was waiting for Weir to start it but had nothing left to run, so Weir gave it a fresh pass.",
                row.RelativePath.TrimEnd('/').Split('/')[^1]);
        }

        return repaired;
    }

    private async Task<List<HandoffLedgerRow>> StuckAsync(CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var stuck = new List<HandoffLedgerRow>();
            foreach (var row in await HandoffLedgerStore.UnfinishedAsync(uow).ConfigureAwait(false))
            {
                if (!await HandoffLedgerStore.HasOpenJobAsync(uow, row).ConfigureAwait(false)
                    && (await _ledger.CurrentStatusAsync(uow, row).ConfigureAwait(false)).State == HandoffLedgerRules.Queued)
                {
                    stuck.Add(row);
                }
            }

            return stuck;
        }
    }

    /// <summary>
    /// The send this hand-off came from, as far as Weir kept it: the ledger row has the folder and ids, and the newest job it ever had
    /// has the manager's callback and library, which a report back needs.
    /// </summary>
    private static async Task<MediaManagerImportEvent> ResendOfAsync(UnitOfWork uow, HandoffLedgerRow row, IntakeLibrary library)
    {
        var origins = await uow.QueryAsync(
            "SELECT payload_json FROM jobs WHERE json_valid(payload_json) AND json_extract(payload_json, '$.origin.source_key') = $source " +
            "AND json_extract(payload_json, '$.origin.handoff_id') = $id ORDER BY id DESC LIMIT 1",
            reader => SqliteValues.GetString(reader, 0),
            ("$source", row.SourceKey),
            ("$id", row.HandoffId)).ConfigureAwait(false);
        var origin = origins.Count > 0 && WireJsonParser.Parse(origins[0]) is WireObject payload ? HandoffOrigin.FromPayload(payload) : null;
        return new MediaManagerImportEvent
        {
            SourceKey = row.SourceKey,
            EventKind = MediaManagerImportEvent.Handoff,
            MediaScope = library.MediaType,
            FilePath = Path.Join(library.WatchedFolder, row.RelativePath),
            HandoffId = row.HandoffId,
            DownloadId = row.DownloadId,
            CallbackPath = origin?.CallbackPath,
            ReleaseName = origin?.ReleaseName,
            LibraryId = origin?.LibraryId,
        };
    }
}
