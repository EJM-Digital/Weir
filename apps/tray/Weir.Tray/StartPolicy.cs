namespace Weir.Tray;

/// <summary>
/// How the tray supervises every start of the server: when a start counts as hung, how long it waits before trying again, how
/// many failures it allows, and when it forgets them. One policy serves the first start, a start after the server crashed and a
/// start the person asked for, as a service manager's restart policy serves them all.
/// </summary>
/// <remarks>
/// The values follow the practice of proven supervisors and are taken from measurements, not guesses (docs/start-supervision.md):
/// <list type="bullet">
/// <item><see cref="FirstProgressWindow"/> and <see cref="StallWindow"/>: a start that stops reporting is declared hung, as a
/// Kubernetes startup probe fails a container that stops answering, after a time measured with margin (the slowest first report
/// was 2.3 s with the CPU oversubscribed twofold).</item>
/// <item><see cref="InitialBackoff"/>, <see cref="BackoffMultiplier"/>, <see cref="MaxBackoff"/> and jitter: capped exponential
/// backoff, as Kubernetes does for CrashLoopBackOff and systemd does with RestartSteps/RestartMaxDelaySec, with the jitter
/// ("equal jitter") that keeps restarts of several programs from landing together.</item>
/// <item><see cref="StartLimitBurst"/> within <see cref="StartLimitInterval"/>: systemd's StartLimitBurst and StartLimitIntervalSec.</item>
/// <item><see cref="StableAfter"/>: the failure count resets after a run that was stable, as Kubernetes resets its back-off after
/// ten minutes and the Windows service manager resets its failure count after a reset period.</item>
/// </list>
/// </remarks>
/// <param name="FirstProgressWindow">How long a started server may take to write its first progress. Before that it is loading: the host unpacking, the runtime starting, anything that scans the file.</param>
/// <param name="StallWindow">How long a server that has reported may stay silent before it is declared hung, unless it asked for longer.</param>
/// <param name="StallCeiling">The most silence the tray believes a server that asks for longer; a server's own request is never believed past this.</param>
/// <param name="InitialBackoff">The wait before the second attempt, before jitter.</param>
/// <param name="BackoffMultiplier">How much longer each further wait is.</param>
/// <param name="MaxBackoff">The longest wait, before jitter.</param>
/// <param name="StartLimitBurst">How many failures within <paramref name="StartLimitInterval"/> end the trying.</param>
/// <param name="StartLimitInterval">The window the failures are counted in.</param>
/// <param name="StableAfter">How long a server must have run, ready, for the earlier failures to be forgotten.</param>
sealed record StartPolicy(
    TimeSpan FirstProgressWindow,
    TimeSpan StallWindow,
    TimeSpan StallCeiling,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    TimeSpan MaxBackoff,
    int StartLimitBurst,
    TimeSpan StartLimitInterval,
    TimeSpan StableAfter)
{
    internal static StartPolicy Default { get; } = new(
        FirstProgressWindow: TimeSpan.FromSeconds(30),
        StallWindow: TimeSpan.FromSeconds(30),
        StallCeiling: TimeSpan.FromHours(1),
        InitialBackoff: TimeSpan.FromSeconds(2),
        BackoffMultiplier: 2,
        MaxBackoff: TimeSpan.FromSeconds(60),
        StartLimitBurst: 5,
        StartLimitInterval: TimeSpan.FromMinutes(15),
        StableAfter: TimeSpan.FromMinutes(10));

    /// <summary>The longest wait after <paramref name="consecutiveFailures"/> failures in a row (from 1), before jitter.</summary>
    internal TimeSpan BackoffCeiling(int consecutiveFailures)
    {
        var steps = Math.Max(0, consecutiveFailures - 1);
        var seconds = InitialBackoff.TotalSeconds * Math.Pow(BackoffMultiplier, steps);
        return TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, seconds));
    }

    /// <summary>
    /// The wait after <paramref name="consecutiveFailures"/> failures in a row: "equal jitter", half of the ceiling plus a random
    /// share of the other half, so the wait is never short enough to hammer and never the same twice.
    /// <paramref name="random01"/> returns a number from 0 up to, not including, 1.
    /// </summary>
    internal TimeSpan Backoff(int consecutiveFailures, Func<double> random01)
    {
        var ceiling = BackoffCeiling(consecutiveFailures);
        return (ceiling / 2) + (ceiling / 2 * Math.Clamp(random01(), 0, 1));
    }

    /// <summary>How long a server that last reported <paramref name="expectWithin"/> may stay silent.</summary>
    internal TimeSpan SilenceAllowed(TimeSpan expectWithin) =>
        expectWithin > StallCeiling ? StallCeiling : expectWithin > StallWindow ? expectWithin : StallWindow;
}
