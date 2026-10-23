using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>Enqueue-or-get by dedupe key.</summary>
public sealed partial class ProcessingJobStore
{
    /// <summary>
    /// Insert a pending job, or return the row already holding <paramref name="dedupeKey"/>. A null <paramref name="runnerCost"/>
    /// costs the job against the resolution budget as <see cref="RunnerCosts"/> says.
    /// </summary>
    /// <exception cref="ArgumentException">The job kind is retired or not a <c>processing.*</c> kind.</exception>
    public Task<ProcessingJob> EnqueueOrGetAsync(
        string dedupeKey,
        string jobKind,
        string? payloadJson = null,
        int maxAttempts = JobQueueRules.DefaultMaxAttempts,
        int? runnerCost = null,
        int priority = 0,
        DateTimeOffset? notBefore = null,
        CancellationToken cancellationToken = default)
    {
        JobKindGuard.ValidateEnqueueJobKind(jobKind);
        ArgumentNullException.ThrowIfNull(dedupeKey);
        return InTransactionAsync(
            (connection, transaction) => EnqueueOrGet(connection, transaction, dedupeKey, jobKind, payloadJson, maxAttempts, runnerCost, priority, notBefore),
            cancellationToken);
    }

    /// <summary>
    /// Queue a job that runs again and again under one key, one run at a time: a run still pending or leased under
    /// <paramref name="dedupeKey"/> is returned as it is, and a finished run gives its key up, keeping its row as history,
    /// so a new run is queued.
    /// </summary>
    /// <exception cref="ArgumentException">The job kind is retired or not a <c>processing.*</c> kind.</exception>
    public Task<ProcessingJob> EnqueueNextRunAsync(
        string dedupeKey,
        string jobKind,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        JobKindGuard.ValidateEnqueueJobKind(jobKind);
        ArgumentNullException.ThrowIfNull(dedupeKey);
        return InTransactionAsync(
            (connection, transaction) =>
            {
                if (GetByDedupeKey(connection, transaction, dedupeKey) is { } last && ProcessingJobStatus.Terminal.Contains(last.Status))
                {
                    Execute(
                        connection,
                        transaction,
                        "UPDATE jobs SET dedupe_key = @dedupe WHERE id = @id",
                        ("@dedupe", JobQueueRules.TombstoneFinishedDedupeKey(last.DedupeKey, last.Id)),
                        ("@id", last.Id));
                }

                return EnqueueOrGet(connection, transaction, dedupeKey, jobKind, payloadJson, JobQueueRules.DefaultMaxAttempts, runnerCost: null, priority: 0);
            },
            cancellationToken);
    }

    /// <summary>
    /// <see cref="EnqueueOrGet(SqliteConnection, SqliteTransaction, string, string, string?, int, int?, int, DateTimeOffset?)"/> on the
    /// caller's unit of work: commits nothing, and announces the new job once the unit commits.
    /// </summary>
    public async Task<ProcessingJob> EnqueueOrGetAsync(
        UnitOfWork uow,
        string dedupeKey,
        string jobKind,
        string? payloadJson,
        int maxAttempts,
        int? runnerCost,
        int priority,
        DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return EnqueueOrGet(uow.Connection, await uow.WriteTransactionAsync().ConfigureAwait(false), dedupeKey, jobKind, payloadJson, maxAttempts, runnerCost, priority, notBefore);
    }

    /// <summary>
    /// Wakes the idle slots of the lane that runs <paramref name="jobKind"/>, for a job that has just become claimable. Called
    /// before the unit of work commits, which is safe: a woken slot claims under BEGIN IMMEDIATE, so its claim waits for the
    /// write lock this unit holds and sees the job once it is committed, or nothing if it rolls back.
    /// </summary>
    internal void WakeWorkers(string jobKind) => _wakeSignals?.WakeFor(jobKind);

    internal ProcessingJob EnqueueOrGet(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dedupeKey,
        string jobKind,
        string? payloadJson,
        int maxAttempts,
        int? runnerCost,
        int priority,
        DateTimeOffset? notBefore = null)
    {
        JobKindGuard.ValidateEnqueueJobKind(jobKind);
        var existing = GetByDedupeKey(connection, transaction, dedupeKey);
        if (existing is not null)
        {
            return existing;
        }

        // not_before is the column a retry's backoff already uses, so a job queued to start later is claimed by
        // exactly the same rule as one that is waiting out a failure (#632).
        // The lookup above and this insert share one write transaction, so the key cannot have been taken in between.
        var inserted = Scalar(
            connection,
            transaction,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, max_attempts, runner_cost, priority, not_before) " +
            "VALUES (@dedupe, @kind, @payload, @status, @max_attempts, @runner_cost, @priority, @not_before) RETURNING id",
            ("@dedupe", dedupeKey),
            ("@kind", jobKind),
            ("@payload", payloadJson),
            ("@status", ProcessingJobStatus.Pending),
            ("@max_attempts", Math.Max(1, maxAttempts)),
            ("@runner_cost", Math.Max(0, runnerCost ?? RunnerCosts.ForJob(connection, transaction, jobKind, payloadJson))),
            ("@priority", priority),
            ("@not_before", notBefore is { } when ? TimestampColumns.Orm(when) : null));
        RecordQueueDepth(connection, transaction);
        AnnounceQueueChange(transaction, jobKind);
        // A job that starts later is claimed by the poll once it is due, so it wakes nobody now.
        if (notBefore is not { } startsAt || startsAt <= _time.GetUtcNow())
        {
            WakeWorkers(jobKind);
        }

        return Get(connection, transaction, Convert.ToInt64(inserted, System.Globalization.CultureInfo.InvariantCulture))!;
    }
}
