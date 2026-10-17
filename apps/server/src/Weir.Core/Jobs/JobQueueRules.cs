using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Time;

namespace Weir.Core.Jobs;

/// <summary>The pure rules behind the job queue: retry backoff, dedupe tombstones and audit wording.</summary>
public static class JobQueueRules
{
    public const int DedupeKeyMaxLength = 512;

    /// <summary>Terminal error text is bounded to this many characters.</summary>
    public const int LastErrorLimit = 10_000;

    public const int DefaultMaxAttempts = 3;

    /// <summary>What an operator cancel writes to <c>last_error</c>.</summary>
    public const string CancelledByOperatorError = "Cancelled by operator before a worker claimed this job.";

    /// <summary>What a look replaced by a newer send of the same file writes to <c>last_error</c>.</summary>
    public const string ReplacedByResendError = "Replaced by a newer send of the same file before a worker claimed this job.";

    /// <summary>
    /// Seconds before a failed attempt may be claimed again: 30 × 2^(<c>attempt_count</c> − 1), at most 1800.
    /// </summary>
    /// <remarks>
    /// The claim has already incremented <c>attempt_count</c> to at least one; a zero or negative count
    /// still yields a value (15 s and less) rather than throwing.
    /// </remarks>
    public static double RetryBackoffSeconds(int attemptCount)
    {
        var exponent = attemptCount - 1;
        if (exponent >= 6)
        {
            return 1800;
        }

        return Math.Min(30 * Math.Pow(2, exponent), 1800);
    }

    /// <summary>Rewrites a cancelled job's dedupe key so the original key is free for a new enqueue.</summary>
    public static string TombstoneCancelledDedupeKey(string original, long jobId) => Tombstone(original, "cancelled", jobId);

    /// <summary>Rewrites a finished run's dedupe key so the next run of the same job can take the key.</summary>
    public static string TombstoneFinishedDedupeKey(string original, long jobId) => Tombstone(original, "ran", jobId);

    private static string Tombstone(string original, string why, long jobId)
    {
        var suffix = $":{why}:{jobId.ToString(CultureInfo.InvariantCulture)}";
        var text = original ?? string.Empty;
        var keep = Math.Max(0, DedupeKeyMaxLength - suffix.Length);
        var baseText = WireStrings.Slice(text, keep);
        return WireStrings.Slice(baseText + suffix, DedupeKeyMaxLength);
    }

    /// <summary>
    /// The <c>last_error</c> written when an operator marks a <c>handler_ok_finalize_failed</c> row completed.
    /// </summary>
    public static string RecoveredFinalizeFailureError(string? previousError, DateTimeOffset when, string recoveredByLabel)
    {
        var previous = (previousError ?? string.Empty).Trim();
        var iso = Timestamp.FromDateTimeOffset(when).IsoFormat('T').Replace("+00:00", "Z", StringComparison.Ordinal);
        var note =
            $"manual_recover_finalize_failure: marked completed at {iso} by {recoveredByLabel} " +
            "(handler was not re-run; row was handler_ok_finalize_failed).";
        var text = previous.Length > 0 ? $"{previous}\n--- {note}" : note;
        return WireStrings.Slice(text, LastErrorLimit);
    }
}

/// <summary>What startup recovery did.</summary>
public sealed record StartupJobRecoveryResult(int ProcessingRequeued, int ProcessingFailed)
{
    public int TotalRecovered => ProcessingRequeued + ProcessingFailed;
}

/// <summary>The status and error a leased row gets when a restart finds it.</summary>
public sealed record StartupRecoveryDecision(string Status, string LastError, bool Requeued);

/// <summary>Pure rules for recovering jobs a restart left leased.</summary>
public static class StartupJobRecovery
{
    public const string ModuleName = "Weir";

    /// <summary>
    /// A leased row at startup belongs to a dead worker: requeue it when attempts remain, or mark it
    /// failed when the lease already consumed the final attempt.
    /// </summary>
    public static StartupRecoveryDecision Decide(int attemptCount, int maxAttempts, DateTimeOffset now)
    {
        var attempts = attemptCount;
        var max = Math.Max(1, maxAttempts);
        var iso = Timestamp.FromDateTimeOffset(now).IsoFormat('T');
        return attempts >= max
            ? new StartupRecoveryDecision(
                ProcessingJobStatus.Failed,
                "This job was interrupted by a Weir restart after its final attempt. " +
                $"Recovered at {iso} and marked failed so the operator can inspect it.",
                Requeued: false)
            : new StartupRecoveryDecision(
                ProcessingJobStatus.Pending,
                "This job was interrupted by a Weir restart. " +
                $"Recovered at {iso} and queued for another safe attempt.",
                Requeued: true);
    }
}
