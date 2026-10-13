using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// When the server is slow to start work that is waiting for a thread (the pool has none free and adds one only every half second or so),
/// every request is slow with it, and nothing in the log says why. The watch queues a small piece of work every second and measures how
/// long it waits to begin; a wait of <see cref="ThreadPoolStallTracker.LateAfter"/> or more is a warning with the pool's own counts, so a
/// run on a small machine leaves the evidence behind. Time is the fake clock's and the pool is a list the test runs by hand.
/// </summary>
public sealed class ThreadPoolWatchTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger<ThreadPoolWatch> _log = new();
    private readonly List<Action> _waiting = [];
    private readonly ThreadPoolCounts _counts = new(Threads: 12, Pending: 40, MinThreads: 2, MaxThreads: 32767, Processors: 2);

    private ThreadPoolWatch Watch() => new(_time, _log, _waiting.Add, () => _counts);

    private void PoolRunsWhatWaits()
    {
        foreach (var work in _waiting.ToArray())
        {
            work();
        }

        _waiting.Clear();
    }

    private void Pass(TimeSpan time) => _time.Advance(time);

    [Fact]
    public void Work_that_begins_at_once_says_nothing()
    {
        var watch = Watch();
        for (var second = 0; second < 10; second++)
        {
            watch.Tick();
            Pass(TimeSpan.FromMilliseconds(50));
            PoolRunsWhatWaits();
            Pass(TimeSpan.FromMilliseconds(950));
        }

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void A_wait_just_under_the_limit_says_nothing()
    {
        var watch = Watch();
        watch.Tick();
        Pass(ThreadPoolStallTracker.LateAfter - TimeSpan.FromMilliseconds(1));

        watch.Tick();

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Work_that_has_waited_the_limit_without_beginning_is_a_warning_with_the_pools_counts()
    {
        var watch = Watch();
        watch.Tick();
        Pass(TimeSpan.FromSeconds(3));

        watch.Tick();

        var (level, message) = Assert.Single(_log.Messages);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("3 s", message, StringComparison.Ordinal);
        Assert.Contains("12 threads", message, StringComparison.Ordinal);
        Assert.Contains("40 pieces of work waiting", message, StringComparison.Ordinal);
        Assert.Contains("starts 2 at once", message, StringComparison.Ordinal);
        Assert.Contains("Processors available: 2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wait_that_goes_on_is_said_once_not_every_second()
    {
        var watch = Watch();
        watch.Tick();
        for (var second = 0; second < 20; second++)
        {
            Pass(TimeSpan.FromSeconds(1));
            watch.Tick();
        }

        Assert.Single(_log.Messages);
    }

    [Fact]
    public void A_wait_that_goes_on_is_said_again_after_the_repeat_time_with_how_long_it_has_lasted()
    {
        var watch = Watch();
        watch.Tick();
        Pass(ThreadPoolStallTracker.LateAfter);
        watch.Tick();
        Pass(ThreadPoolStallTracker.RepeatEvery);
        watch.Tick();

        Assert.Equal(2, _log.Messages.Count(message => message.Level == LogLevel.Warning));
        Assert.Contains("still", _log.Messages[1].Message, StringComparison.Ordinal);
        Assert.Contains("32 s", _log.Messages[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void When_work_begins_promptly_again_it_says_how_long_it_lasted_and_the_longest_wait()
    {
        var watch = Watch();
        watch.Tick();
        Pass(TimeSpan.FromSeconds(5));
        watch.Tick();
        Pass(TimeSpan.FromSeconds(4));
        PoolRunsWhatWaits();
        Pass(TimeSpan.FromSeconds(1));

        watch.Tick();

        var (level, message) = _log.Messages[^1];
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("promptly again", message, StringComparison.Ordinal);
        Assert.Contains("9 s", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wait_that_began_and_ended_inside_the_limit_is_never_mentioned()
    {
        var watch = Watch();
        watch.Tick();
        Pass(TimeSpan.FromMilliseconds(1_500));
        PoolRunsWhatWaits();
        Pass(TimeSpan.FromSeconds(1));
        watch.Tick();
        Pass(TimeSpan.FromSeconds(1));
        PoolRunsWhatWaits();

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void A_second_stall_is_said_as_a_new_one()
    {
        var watch = Watch();
        watch.Tick();
        Pass(TimeSpan.FromSeconds(3));
        watch.Tick();
        PoolRunsWhatWaits();
        Pass(TimeSpan.FromSeconds(1));
        watch.Tick();
        Pass(TimeSpan.FromSeconds(4));
        watch.Tick();

        Assert.Equal(2, _log.Messages.Count(message => message.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task The_service_starts_and_stops_on_a_thread_of_its_own()
    {
        using var watch = new ThreadPoolWatch(TimeProvider.System, _log);

        await watch.StartAsync(CancellationToken.None);
        await watch.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(_log.Messages, message => message.Level >= LogLevel.Warning);
    }
}
