using Weir.Core.Jobs;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Finding the hand-offs that have not ended and have nothing left to run.</summary>
public sealed partial class HandoffLedgerStore
{
    /// <summary>Every hand-off whose row has not reached a final state, oldest first.</summary>
    public static Task<List<HandoffLedgerRow>> UnfinishedAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var terminal = string.Join(", ", HandoffLedgerRules.TerminalStates.Select(state => $"'{state}'"));
        return uow.QueryAsync($"SELECT {LedgerColumns} FROM media_manager_handoffs WHERE state NOT IN ({terminal}) ORDER BY id", ReadLedger);
    }

    /// <summary>
    /// Whether any job that is waiting or running names this hand-off in its payload: its own pass, or the second look a pass booked
    /// for a source that was gone, which carries the hand-off's origin but not its dedupe key.
    /// </summary>
    public static async Task<bool> HasOpenJobAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        if ((await JobsForAsync(uow, row).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }

        var named = await uow.QueryAsync(
            "SELECT 1 FROM jobs WHERE status IN ($pending, $leased) AND json_valid(payload_json) " +
            "AND json_extract(payload_json, '$.origin.source_key') = $source AND json_extract(payload_json, '$.origin.handoff_id') = $id LIMIT 1",
            reader => reader.GetInt32(0),
            ("$pending", ProcessingJobStatus.Pending),
            ("$leased", ProcessingJobStatus.Leased),
            ("$source", row.SourceKey),
            ("$id", row.HandoffId)).ConfigureAwait(false);
        return named.Count > 0;
    }
}
