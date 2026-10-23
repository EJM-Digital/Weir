using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>What cancelling one queued job did: whether it could, and the hand-off it ended, if any.</summary>
public sealed record PendingJobCancelResult(JobActionOutcome Outcome, HandoffLedgerRow? EndedHandoff = null);

/// <summary>
/// Cancelling one queued job from the jobs list in System › Logs (<c>POST /processing/jobs/{id}/cancel-pending</c>). Only a pending job can
/// be cancelled, and its dedupe key becomes a tombstone so a later enqueue may reuse it.
/// <para>
/// Cancelling changes more than the job row (#643): otherwise the file would read "Waiting" for ever or be queued again by
/// the next scan, and a hand-off would keep answering "queued" to the media manager. A remux pass's file reads cancelled,
/// and a hand-off with nothing else left to run ends the way the manager's own cancel ends it. All of this happens in the
/// caller's unit of work, in one transaction.
/// </para>
/// </summary>
public sealed class PendingJobCancellation
{
    private readonly HandoffLedgerStore _ledger;
    private readonly HandoffCompletionReporter _reporter;
    private readonly FileStateStore _files;
    private readonly DataChangePublisher _changes;

    public PendingJobCancellation(HandoffLedgerStore ledger, HandoffCompletionReporter reporter, FileStateStore files, DataChangePublisher changes)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    public async Task<PendingJobCancelResult> CancelAsync(UnitOfWork uow, long jobId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var found = await uow.QuerySingleAsync(
            "SELECT id, dedupe_key, job_kind, payload_json, status, created_at FROM jobs WHERE id = $id",
            reader => new PendingJobRow(
                SqliteValues.GetInt64(reader, 0),
                SqliteValues.GetString(reader, 1),
                SqliteValues.GetString(reader, 2),
                SqliteValues.GetStringOrNull(reader, 3),
                SqliteValues.GetString(reader, 4),
                TimestampColumns.Parse(reader.GetValue(5))),
            ("$id", jobId)).ConfigureAwait(false);
        if (found is null)
        {
            return new PendingJobCancelResult(JobActionOutcome.NotFound);
        }

        if (found.Status != ProcessingJobStatus.Pending)
        {
            return new PendingJobCancelResult(JobActionOutcome.WrongStatus);
        }

        await uow.ExecuteAsync(
            "UPDATE jobs SET dedupe_key = $dedupe, status = $status, lease_owner = NULL, lease_expires_at = NULL, " +
            "last_error = $error, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$dedupe", JobQueueRules.TombstoneCancelledDedupeKey(found.DedupeKey, found.Id)),
            ("$status", ProcessingJobStatus.Cancelled),
            ("$error", JobQueueRules.CancelledByOperatorError),
            ("$id", found.Id)).ConfigureAwait(false);
        _changes.PublishQueueChangeOnCommit(uow, found.Kind);

        var payload = Parse(found.Payload);
        if (found.Kind == RemuxPassOutcomes.JobKind &&
            payload?.Get("library_id") is WireInteger library && library.Value > 0 &&
            payload.Get("relative_media_path") is WireString { Value.Length: > 0 } path)
        {
            await _files.MarkCancelledAsync(uow, (long)library.Value, path.Value, CancelledFileReasons.InWeir).ConfigureAwait(false);
        }

        if (HandoffOrigin.FromPayload(payload) is { HandoffId: { Length: > 0 } handoffId } origin)
        {
            var handoff = await HandoffLedgerStore.FindAsync(uow, origin.SourceKey, handoffId).ConfigureAwait(false);
            if (handoff is not null)
            {
                if (payload!.Get("relative_media_path") is WireString { Value.Length: > 0 } targetPath)
                {
                    // So a later file of the same pack finishing still sees this one settled, instead of waiting on it
                    // forever; and so cancelling the pack's last unresolved file still reports the ones that delivered.
                    await _reporter.StageReportAfterCancellationAsync(uow, origin, handoff, targetPath.Value).ConfigureAwait(false);
                }

                if (await _ledger.SettleAfterJobCancelledAsync(
                    uow, handoff, found.CreatedAt ?? DateTimeOffset.MinValue, HandoffLedgerRules.CancelledInWeirMessage).ConfigureAwait(false))
                {
                    return new PendingJobCancelResult(JobActionOutcome.Ok, handoff);
                }
            }
        }

        return new PendingJobCancelResult(JobActionOutcome.Ok);
    }

    private sealed record PendingJobRow(long Id, string DedupeKey, string Kind, string? Payload, string Status, DateTimeOffset? CreatedAt);

    private static WireObject? Parse(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            return WireJsonParser.Parse(payloadJson) as WireObject;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }
}
