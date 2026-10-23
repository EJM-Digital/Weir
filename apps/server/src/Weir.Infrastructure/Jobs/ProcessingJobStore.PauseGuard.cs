using Weir.Core.Jobs;
using Weir.Core.Settings;
using Weir.Core.Time;

namespace Weir.Infrastructure.Jobs;

/// <summary>A second, separate check of the pause for a job a worker has already claimed.</summary>
public sealed partial class ProcessingJobStore
{
    /// <summary>
    /// Whether the pause forbids <paramref name="jobKind"/> right now. The claim already refuses work while paused; this reads
    /// the pause again, on its own, so a job claimed by mistake is caught before its handler touches a file.
    /// </summary>
    public Task<bool> PauseForbidsAsync(string jobKind, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKind);
        return ReadAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT processing_paused, processing_paused_until, scan_while_paused FROM suite_settings WHERE id = 1";
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var until = TimestampColumns.Parse(reader.GetValue(1));
                var pause = PauseState.Resolve(
                    WorkAdmissionReader.Bool(reader.GetValue(0)),
                    until is { } at ? Timestamp.FromUtc(at.UtcDateTime) : null,
                    WorkAdmissionReader.Bool(reader.GetValue(2)),
                    now.UtcDateTime);
                return !new WorkAdmission(pause, new HashSet<long>()).AllowsJobKind(jobKind);
            },
            cancellationToken);
    }

    /// <summary>
    /// Puts a job this owner holds back to pending as if it had never been claimed: the attempt the claim counted is given back.
    /// Returns <see langword="false"/> when the owner no longer holds it.
    /// </summary>
    public Task<bool> ReleaseClaimedAsync(long jobId, string leaseOwner, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseOwner);
        return InTransactionAsync(
            (connection, transaction) =>
            {
                var job = Get(connection, transaction, jobId);
                if (!HoldsLease(job, leaseOwner, when))
                {
                    return false;
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE jobs SET status = @status, lease_owner = NULL, lease_expires_at = NULL, attempt_count = MAX(attempt_count - 1, 0), " +
                    "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                    ("@status", ProcessingJobStatus.Pending),
                    ("@id", jobId));
                RecordQueueDepth(connection, transaction);
                AnnounceQueueChange(transaction, job.JobKind);
                return true;
            },
            cancellationToken);
    }
}
