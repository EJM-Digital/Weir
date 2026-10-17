using Microsoft.Extensions.Time.Testing;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// A fake clock that counts the timers set on it. Code that waits on the clock sets a timer before it parks, so a test that has seen
/// the count rise knows the code is parked there, and can move the clock on (or hold it still) knowing nothing is still on its way
/// to the wait.
/// </summary>
internal sealed class TimerWatchingClock(FakeTimeProvider clock) : TimeProvider
{
    private int _timers;

    /// <summary>How many timers have been set on the clock so far.</summary>
    public int TimersSet => Volatile.Read(ref _timers);

    /// <summary>The fake clock underneath, for moving time on.</summary>
    public FakeTimeProvider Fake => clock;

    public void Advance(TimeSpan by) => clock.Advance(by);

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override long GetTimestamp() => clock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timers);
        return clock.CreateTimer(callback, state, dueTime, period);
    }
}
