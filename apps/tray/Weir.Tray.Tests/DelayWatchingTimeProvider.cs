using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

namespace Weir.Tray.Tests;

/// <summary>
/// Fake time that reports each delay the code under test starts, and each time it sets a timer to fire later. A test waits
/// for the next delay, so it knows the code is parked on the clock, then advances the clock: every step waits on a signal,
/// never on real time. A timer that is created or set to never fire is not a delay.
/// </summary>
internal sealed class DelayWatchingTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

    public Task<TimeSpan> NextDelay() => _delays.Reader.ReadAsync().AsTask();

    /// <summary>
    /// Completes when a delay is waiting to be taken, without taking it, so a wait that is given up on (because the code under test
    /// finished first) never leaves a read behind to take the next delay of whatever the clock is used for next.
    /// </summary>
    public Task<bool> DelayWaitingAsync() => _delays.Reader.WaitToReadAsync().AsTask();

    /// <summary>Takes the delay that is waiting, if there is one.</summary>
    public bool TryTakeDelay(out TimeSpan delay) => _delays.Reader.TryRead(out delay);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Report(dueTime);
        return new ReportingTimer(timer, Report);
    }

    private void Report(TimeSpan dueTime)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            _delays.Writer.TryWrite(dueTime);
        }
    }

    private sealed class ReportingTimer(ITimer inner, Action<TimeSpan> report) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = inner.Change(dueTime, period);
            report(dueTime);
            return changed;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
