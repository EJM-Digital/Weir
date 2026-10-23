using System.Diagnostics.CodeAnalysis;
using Weir.Core.Jobs;
using Weir.Core.Json;

namespace Weir.Infrastructure.Jobs;

/// <summary>Lease renewal, completion and failure (with retry and backoff) of a claimed row.</summary>
public sealed partial class ProcessingJobStore
{
    /// <summary>
    /// Extend the lease of a row this owner still holds, so a handler that outlives its original lease
    /// (a long remux, for instance) is never reclaimed by another worker while it is still running
    /// (#540 item 1). A worker calls it on a heartbeat, well inside the lease, while the handler runs.
    /// Returns <see langword="false"/> when this owner has lost the lease (already completed,
    /// failed, or reclaimed after running unrenewed past its expiry), in which case the caller should
    /// stop renewing and let completion/failure fail its own lease check.
    /// </summary>
    public Task<bool> RenewLeaseAsync(long jobId, string leaseOwner, DateTimeOffset newLeaseExpiresAt, DateTimeOffset when, CancellationToken cancellationToken = default)
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
                    "UPDATE jobs SET lease_expires_at = @lease_exp, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                    ("@lease_exp", TimestampColumns.Adapter(newLeaseExpiresAt)),
                    ("@id", jobId));
                return true;
            },
            cancellationToken);
    }

    /// <summary>Mark the job completed; only for the owning, unexpired lease.</summary>
    public Task<bool> CompleteClaimedAsync(long jobId, string leaseOwner, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var when = now ?? _time.GetUtcNow();
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
                    "UPDATE jobs SET status = @status, lease_owner = NULL, lease_expires_at = NULL, last_error = NULL, " +
                    "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                    ("@status", ProcessingJobStatus.Completed),
                    ("@id", jobId));
                _metrics.RecordJobEvent(MetricsModule, "completed");
                RecordQueueDepth(connection, transaction);
                AnnounceQueueChange(transaction, job.JobKind);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// After a failed attempt, requeue with backoff, or mark failed once the attempts are used.
    /// </summary>
    public Task<bool> FailClaimedAsync(long jobId, string leaseOwner, string errorMessage, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        return InTransactionAsync(
            (connection, transaction) =>
            {
                var job = Get(connection, transaction, jobId);
                if (job is null || !HoldsLease(job, leaseOwner, when))
                {
                    return false;
                }

                if (job.AttemptCount >= job.MaxAttempts)
                {
                    Execute(
                        connection,
                        transaction,
                        "UPDATE jobs SET last_error = @error, lease_owner = NULL, lease_expires_at = NULL, status = @status, " +
                        "not_before = NULL, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                        ("@error", errorMessage),
                        ("@status", ProcessingJobStatus.Failed),
                        ("@id", jobId));
                    _metrics.RecordJobEvent(MetricsModule, "failed");
                }
                else
                {
                    var delay = JobQueueRules.RetryBackoffSeconds(job.AttemptCount);
                    Execute(
                        connection,
                        transaction,
                        "UPDATE jobs SET last_error = @error, lease_owner = NULL, lease_expires_at = NULL, status = @status, " +
                        "not_before = @not_before, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                        ("@error", errorMessage),
                        ("@status", ProcessingJobStatus.Pending),
                        ("@not_before", TimestampColumns.Orm(when + TimeSpan.FromTicks((long)Math.Round(delay * TimeSpan.TicksPerSecond)))),
                        ("@id", jobId));
                }

                RecordQueueDepth(connection, transaction);
                AnnounceQueueChange(transaction, job.JobKind);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// For a handler that succeeded when recording its completion did not. Terminal and not claimable;
    /// <c>attempt_count</c> is unchanged.
    /// </summary>
    public Task<bool> FailLeasedAfterCompleteFailureAsync(long jobId, string leaseOwner, string errorMessage, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
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
                    "UPDATE jobs SET status = @status, lease_owner = NULL, lease_expires_at = NULL, last_error = @error, " +
                    "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                    ("@status", ProcessingJobStatus.HandlerOkFinalizeFailed),
                    ("@error", WireStrings.Slice(errorMessage, JobQueueRules.LastErrorLimit)),
                    ("@id", jobId));
                _metrics.RecordJobEvent(MetricsModule, "failed");
                RecordQueueDepth(connection, transaction);
                AnnounceQueueChange(transaction, job.JobKind);
                return true;
            },
            cancellationToken);
    }

    private static bool HoldsLease([NotNullWhen(true)] ProcessingJob? job, string leaseOwner, DateTimeOffset when) =>
        job is not null &&
        job.Status == ProcessingJobStatus.Leased &&
        job.LeaseOwner == leaseOwner &&
        job.LeaseExpiresAt is { } expires &&
        expires >= when;
}
