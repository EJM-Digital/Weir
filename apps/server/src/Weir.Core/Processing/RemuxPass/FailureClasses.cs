using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Text;

namespace Weir.Core.Processing.RemuxPass;

/// <summary>
/// Why a file failed, in the terms a retry policy can act on.
/// </summary>
public static class ProcessingFailureClasses
{
    /// <summary>Rejected before any work started. Retrying reaches the same conclusion.</summary>
    public const string Preflight = "preflight";

    /// <summary>Work started and the process failed. Retrying is reasonable.</summary>
    public const string Execution = "execution";

    /// <summary>A guardrail deliberately stopped the file. Never retried automatically.</summary>
    public const string Guardrail = "guardrail";

    /// <summary>The rules themselves left nothing to keep, and no media manager was asked about it. Terminal.</summary>
    public const string Rules = "rules";

    /// <summary>Something Weir could not attribute. Terminal.</summary>
    public const string Unknown = "unknown";

    /// <summary>Map a pass outcome onto the retry vocabulary.</summary>
    public static string Classify(string? outcome) => WireStrings.Strip(outcome ?? string.Empty).ToLowerInvariant() switch
    {
        RemuxPassOutcomes.FailedBeforeExecution => Preflight,
        RemuxPassOutcomes.FailedDuringExecution => Execution,
        RemuxPassOutcomes.SkippedGuardrail => Guardrail,
        _ => Unknown,
    };

    /// <summary>Whether the library retries this class; guardrail and unknown never are.</summary>
    public static bool IsRetryable(string failureClass, bool retryPreflightFailures, bool retryExecutionFailures)
    {
        var value = WireStrings.Strip(failureClass).ToLowerInvariant();
        return value switch
        {
            Preflight => retryPreflightFailures,
            Execution => retryExecutionFailures,
            _ => false,
        };
    }

    /// <summary>Retry delay: doubling from the base, capped at an hour.</summary>
    public static long BackoffSecondsForAttempt(long attempt, long baseSeconds)
    {
        var @base = Math.Max(1, baseSeconds);
        var exponent = Math.Max(0, attempt - 1);
        if (exponent >= 16)
        {
            return 3600;
        }

        return Math.Min(3600, @base * (1L << (int)exponent));
    }
}

/// <summary>Whether this failure will be retried, and the sentence explaining it.</summary>
public sealed record RetryDecision(bool WillRetry, DateTimeOffset? NextRetryAt, string Reason);

/// <summary>The library's automatic retry policy applied to one failure.</summary>
public static class RetryPolicy
{
    /// <summary>
    /// Retry or give up, based on the failure class and the workflow's attempt limit. <paramref name="attemptsMade"/> counts
    /// the attempt that just failed, so a limit of N allows exactly N attempts in all.
    /// </summary>
    public static RetryDecision DecideRetry(ProcessingLibraryRecord library, string failureClass, long attemptsMade, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        var value = WireStrings.Strip(failureClass).ToLowerInvariant();
        if (!ProcessingFailureClasses.IsRetryable(value, library.RetryPreflightFailures, library.RetryExecutionFailures))
        {
            return new RetryDecision(false, null, NotRetryableReason(value, library));
        }

        var maxAttempts = Math.Max(1, library.MaxAttempts);
        if (attemptsMade >= maxAttempts)
        {
            return new RetryDecision(
                false,
                null,
                $"Weir tried this file {Plural.Of(attemptsMade, "time")} and stopped, because the {library.Name} " +
                $"workflow allows {maxAttempts.ToString(CultureInfo.InvariantCulture)}. You can still start it again by hand.");
        }

        var delay = ProcessingFailureClasses.BackoffSecondsForAttempt(attemptsMade, library.RetryBackoffSeconds);
        var minutes = delay / 60;
        return new RetryDecision(
            true,
            now + TimeSpan.FromSeconds(delay),
            $"This failed and Weir will try again in about {Plural.Of(minutes == 0 ? 1 : minutes, "minute")} " +
            $"(attempt {(attemptsMade + 1).ToString(CultureInfo.InvariantCulture)} of {maxAttempts.ToString(CultureInfo.InvariantCulture)}).");
    }

    private static string NotRetryableReason(string failureClass, ProcessingLibraryRecord library) => failureClass switch
    {
        ProcessingFailureClasses.Preflight =>
            "This file was rejected before any work started, so trying again would reach the same " +
            "conclusion. Fix what the reason describes, then start it again by hand.",
        ProcessingFailureClasses.Guardrail =>
            "A safety check stopped this file. Weir does not retry those automatically — the check " +
            "is the answer, not something to get past.",
        ProcessingFailureClasses.Execution =>
            $"This failed while being processed, and the {library.Name} workflow is set not to retry those. " +
            "You can start it again by hand.",
        _ => "Weir could not work out why this failed, so it is not retrying automatically. You can start it again by hand.",
    };
}
