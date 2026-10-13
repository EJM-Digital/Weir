namespace Weir.Tray;

/// <summary>What to do after a failed start.</summary>
/// <param name="Attempt">The number of the attempt that failed, from 1 in this run of attempts.</param>
/// <param name="Reason">Why it failed, in the words the log uses.</param>
/// <param name="GiveUp">The start limit has been reached: no further attempt is made.</param>
/// <param name="Delay">How long to wait before the next attempt; zero when giving up.</param>
/// <param name="FailuresInWindow">How many failures, this one included, fall inside the start-limit window.</param>
readonly record struct StartDecision(int Attempt, string Reason, bool GiveUp, TimeSpan Delay, int FailuresInWindow);

/// <summary>
/// The count of failed starts and what follows each: a retry after a capped, jittered wait, or giving up once
/// <see cref="StartPolicy.StartLimitBurst"/> failures fall within <see cref="StartPolicy.StartLimitInterval"/>. A server that ran
/// ready for <see cref="StartPolicy.StableAfter"/> before it failed starts the count over, so a Weir that crashes once a week
/// is never closer to giving up for it. The first start, a start after a crash and a start the person asked for all keep one
/// count here; it holds no process and no clock of its own, so a test drives it with fake time.
/// </summary>
sealed class StartSupervision
{
    private readonly StartPolicy _policy;
    private readonly TimeProvider _time;
    private readonly Func<double> _random01;
    private readonly List<(long At, int Attempt, string Reason)> _failures = [];
    private int _consecutive;
    private long? _readyAt;

    internal StartSupervision(StartPolicy policy, TimeProvider time, Func<double>? random01 = null)
    {
        _policy = policy;
        _time = time;
        _random01 = random01 ?? Random.Shared.NextDouble;
    }

    /// <summary>The number of the attempt about to be made or under way, from 1 in this run of attempts.</summary>
    internal int Attempt => _consecutive + 1;

    /// <summary>Why each earlier attempt in this run failed, as "attempt 1: …", oldest first.</summary>
    internal IReadOnlyList<string> Failures => [.. _failures.Select(failure => $"attempt {failure.Attempt}: {failure.Reason}")];

    /// <summary>Forgets every failure: a person asked for a new start, or the server has been stable.</summary>
    internal void Reset()
    {
        _failures.Clear();
        _consecutive = 0;
        _readyAt = null;
    }

    /// <summary>The server answered ready. How long it then runs decides whether this run of attempts is forgotten.</summary>
    internal void Ready() => _readyAt = _time.GetTimestamp();

    /// <summary>Records that the attempt now under way failed for <paramref name="reason"/>, and says what follows.</summary>
    internal StartDecision Failed(string reason)
    {
        if (_readyAt is { } ready && _time.GetElapsedTime(ready) >= _policy.StableAfter)
        {
            Reset();
        }

        _readyAt = null;
        var now = _time.GetTimestamp();
        var attempt = Attempt;
        _consecutive++;
        _failures.Add((now, attempt, reason));
        _failures.RemoveAll(failure => _time.GetElapsedTime(failure.At, now) > _policy.StartLimitInterval);
        var inWindow = _failures.Count;
        if (inWindow >= _policy.StartLimitBurst)
        {
            return new StartDecision(attempt, reason, GiveUp: true, TimeSpan.Zero, inWindow);
        }

        return new StartDecision(attempt, reason, GiveUp: false, _policy.Backoff(_consecutive, _random01), inWindow);
    }
}
