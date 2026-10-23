namespace Weir.Infrastructure.Runtime;

/// <summary>
/// A time limit that counts only the time the process could run. Its token is cancelled once the limit has passed on the clock
/// less the silences <see cref="ProcessPulse"/> noted: the computer asleep, a machine saved or paused, a container frozen. Such a
/// stretch used up none of the work's time, so a file is not failed for it, while a run that overran with Weir awake is still stopped.
/// </summary>
/// <remarks>
/// A timer is set for the limit. When it fires, the silence inside the time since the limit was started is taken away; if what is
/// left is under the limit, the timer is set again for the remainder. The same rule holds whichever way the platform's timers treat
/// a sleep: Windows' fire at once on waking, having counted it, and Linux's have not counted it, and in both the awake time is the
/// time that passed less the silence.
/// </remarks>
public sealed class AwakeDeadline : IDisposable
{
    private readonly TimeSpan? _limit;
    private readonly TimeProvider _time;
    private readonly ProcessPulse? _pulse;
    private readonly CancellationTokenSource _source = new();
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private DateTimeOffset _startedAt;
    private bool _disposed;

    /// <param name="limit">How much awake time the work is given; none means it is never cut off.</param>
    /// <param name="time">The clock the limit runs on.</param>
    /// <param name="pulse">Says when the process could not run; without it, every moment counts.</param>
    /// <param name="started">Whether the limit starts counting now; otherwise it waits for <see cref="Restart"/>.</param>
    public AwakeDeadline(TimeSpan? limit, TimeProvider time, ProcessPulse? pulse, bool started = true)
    {
        ArgumentNullException.ThrowIfNull(time);
        _limit = limit;
        _time = time;
        _pulse = pulse;
        _timer = time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (started)
        {
            Restart();
        }
    }

    /// <summary>Cancelled when the limit has run out.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Starts the count again from now, as when the work shows it is still going.</summary>
    public void Restart()
    {
        if (_limit is not { } limit)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _startedAt = _time.GetUtcNow();
            _timer.Change(limit, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }

        _source.Dispose();
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): the timer's callback, which can already be queued when the work ends and so must find the deadline disposed or unlimited.</summary>
    internal void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _limit is not { } limit)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var away = _pulse?.AwayWithin(_startedAt, now).Within ?? TimeSpan.Zero;
            var awake = now - _startedAt - away;
            if (awake < limit)
            {
                _timer.Change(limit - awake, Timeout.InfiniteTimeSpan);
                return;
            }

            // Under the gate, so that Dispose cannot release the source between the check above and this.
            _source.Cancel();
        }
    }
}
