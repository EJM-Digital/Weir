using Microsoft.Extensions.Time.Testing;

namespace Weir.Infrastructure.Tests;

public sealed class TimerWatchingClockTests
{
    /// <summary>
    /// A test that sees the count rise moves the clock on at once, so the timer must already be on the clock under it: counted first, a timer
    /// still being registered misses that move and then waits a whole interval later than the test expects, for ever.
    /// </summary>
    [Fact]
    public void The_count_rises_once_the_clock_under_it_holds_the_timer_not_before()
    {
        var underneath = new RegistrationProbe();
        var watching = new TimerWatchingClock(underneath);
        underneath.Watching = watching;

        using var timer = watching.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        Assert.Equal(0, underneath.CountOnceRegistered);
        Assert.Equal(1, watching.TimersSet);
    }

    [Fact]
    public void A_timer_set_through_it_fires_when_the_clock_moves_on()
    {
        var underneath = new FakeTimeProvider();
        var watching = new TimerWatchingClock(underneath);
        var fired = 0;

        using var timer = watching.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        watching.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, fired);
    }

    private sealed class RegistrationProbe : FakeTimeProvider
    {
        public TimerWatchingClock? Watching { get; set; }

        /// <summary>What the count was when the timer had been put on this clock.</summary>
        public int CountOnceRegistered { get; private set; } = -1;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            CountOnceRegistered = Watching!.TimersSet;
            return timer;
        }
    }
}
