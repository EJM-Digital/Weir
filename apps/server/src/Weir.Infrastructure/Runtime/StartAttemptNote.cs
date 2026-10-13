using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// What the Windows tray tells a server it starts again after an earlier start failed or hung, through two environment variables
/// of that process only. The failed server could not say anything (it had hung, or been killed), so the next one puts the
/// failure in <c>weir.log</c> as a warning: a start that only worked on a retry is never invisible, to System > Logs or to a
/// suite that reads the log for warnings. The tray writes the same words to its own log.
/// </summary>
public static class StartAttemptNote
{
    /// <summary>The number of this start in the tray's run of attempts, from 1.</summary>
    public const string AttemptVariable = "WEIR_START_ATTEMPT";

    /// <summary>Why each earlier attempt failed, in the tray's words, joined with "; ".</summary>
    public const string FailuresVariable = "WEIR_START_FAILURES";

    private const int LongestFailures = 1000;

    /// <summary>Logs the warning when this start is a retry. Nothing otherwise.</summary>
    public static void WarnIfRetry(RuntimeEnvironment runtime, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        if (!int.TryParse(runtime.Get(AttemptVariable), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var attempt)
            || attempt < 2)
        {
            return;
        }

        var failures = (runtime.Get(FailuresVariable) ?? string.Empty).Trim();
        if (failures.Length > LongestFailures)
        {
            failures = failures[..LongestFailures] + "…";
        }

        logger.LogWarning(
            "Weir is starting again: this is start attempt {Attempt}, because the earlier attempt failed. {Failures}",
            attempt,
            failures.Length == 0 ? "The tray did not say why." : failures);
    }
}
