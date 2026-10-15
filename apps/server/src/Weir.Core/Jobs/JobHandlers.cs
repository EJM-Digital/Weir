using Weir.Core.Json;

namespace Weir.Core.Jobs;

/// <summary>What a person who set up alerts hears about a job whose handler returned without throwing.</summary>
public enum JobAlert
{
    /// <summary>That the job finished, as the job's own end says.</summary>
    Finished,

    /// <summary>That the work failed and Weir will not try again, though the job itself ended normally (a file the tools could not finish).</summary>
    FailedForGood,

    /// <summary>Nothing: the work is not over (waiting, trying again) or was not done (left alone, already done).</summary>
    Nothing,
}

/// <summary>
/// The view passed to a job handler after a successful claim, outside the claim transaction. It is immutable apart from
/// <see cref="Alert"/>, which a handler sets when the job's end does not say what happened to the work.
/// </summary>
public sealed record JobWorkContext(
    long Id,
    string JobKind,
    string? PayloadJson,
    string LeaseOwner,
    int AttemptCount = 1,
    int MaxAttempts = 1)
{
    /// <summary>What the alert channels hear once the handler returns; <see cref="JobAlert.Finished"/> unless the handler says otherwise.</summary>
    public JobAlert Alert { get; set; } = JobAlert.Finished;
}

/// <summary>
/// Runs one job kind. Throw to fail the attempt (the worker retries or marks it failed and writes
/// the Activity entry); throw <see cref="AlreadyRecordedFailureException"/> when the handler has
/// already written its own Activity entry.
/// </summary>
/// <remarks>
/// The cancellation token fires only when Weir is shutting down. A handler that stops because of it
/// leaves its row leased, and startup recovery requeues it on the next start, exactly as when the
/// process is killed mid-job.
/// </remarks>
public interface IJobHandler
{
    /// <summary>The <c>processing.*</c> job kind this handler runs.</summary>
    string JobKind { get; }

    Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The job handlers this server can run, keyed by job kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kinds without a handler are never claimed.</b> A worker claims only rows whose kind is registered
/// here, plus rows no worker may ever run (retired or unprefixed kinds), which it claims only to refuse
/// them with a stored reason. Any other row stays <c>pending</c>, untouched, rather than being failed for
/// lack of a handler (#514).
/// </para>
/// </remarks>
public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers;

    public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.JobKind, handler))
            {
                throw new ArgumentException($"Two job handlers are registered for job_kind {WireStrings.Repr(handler.JobKind)}.", nameof(handlers));
            }
        }

        JobKindGuard.ValidateHandlerRegistry(_handlers.Keys);
    }

    public static JobHandlerRegistry Empty { get; } = new([]);

    /// <summary>The registered kinds, in ordinal order.</summary>
    public IReadOnlyList<string> JobKinds => [.. _handlers.Keys.Order(StringComparer.Ordinal)];

    public bool Contains(string jobKind) => _handlers.ContainsKey(jobKind);

    public IJobHandler? Find(string jobKind) => _handlers.GetValueOrDefault(jobKind);
}

/// <summary>Timing rules shared by the periodic enqueue loops.</summary>
public static class PeriodicSchedule
{
    /// <summary>How long a periodic enqueuer waits after a failed tick before trying again.</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(2);

    /// <summary>How many whole intervals elapsed after the next due time.</summary>
    public static int MissedDueRunCount(double nowSeconds, double nextRunSeconds, double intervalSeconds)
    {
        var interval = Math.Max(1.0, intervalSeconds);
        if (nowSeconds <= nextRunSeconds)
        {
            return 0;
        }

        return (int)Math.Floor((nowSeconds - nextRunSeconds) / interval);
    }

    /// <summary>How long the scheduler sleeps: until the nearest due scope, capped by the polling cadence.</summary>
    public static double NextSchedulerSleepSeconds(double nowSeconds, double nextRunMovie, double nextRunTv, double pollSeconds)
    {
        var nextDue = Math.Min(nextRunMovie, nextRunTv);
        var untilDue = Math.Max(0.25, nextDue - nowSeconds);
        return Math.Max(0.25, Math.Min(pollSeconds, untilDue));
    }

    /// <summary>A library's scan cadence, 10 s .. 7 days (300 s when unset).</summary>
    public static double WatchedFolderScanIntervalSeconds(long? scanIntervalSeconds) =>
        Math.Max(10.0, Math.Min(scanIntervalSeconds ?? 300, 7 * 24 * 3600));
}
