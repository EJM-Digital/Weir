using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// A time limit that guards real work counts only the time the process could run. A computer that sleeps, a machine that is saved or
/// a container that is frozen while a tool runs does not use up the tool's time, and a tool that overran while Weir was awake is still
/// stopped. The clock is the fake one and the sleeps are the silences <see cref="ProcessPulse"/> noted.
/// </summary>
public sealed class AwakeDeadlineTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly ProcessPulse _pulse;

    public AwakeDeadlineTests() => _pulse = new ProcessPulse(_time);

    public void Dispose() => _pulse.Dispose();

    /// <summary>Weir is up for <paramref name="time"/>, a second at a time.</summary>
    private void Awake(TimeSpan time)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += TimeSpan.FromSeconds(1))
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _pulse.Note();
        }
    }

    /// <summary>The computer sleeps for <paramref name="time"/>: nothing runs, then the pulse notes the time again on waking.</summary>
    private void Sleeps(TimeSpan time, bool pulseWokeFirst = true)
    {
        _time.Advance(time);
        if (pulseWokeFirst)
        {
            _pulse.Note();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_sleep_longer_than_the_limit_does_not_use_up_the_limit(bool pulseWokeFirst)
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);
        Awake(TimeSpan.FromSeconds(10));

        Sleeps(TimeSpan.FromHours(2), pulseWokeFirst);

        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void After_a_sleep_the_time_that_was_awake_before_it_still_counts()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);
        Awake(TimeSpan.FromSeconds(10));
        Sleeps(TimeSpan.FromHours(2));

        Awake(TimeSpan.FromSeconds(49));
        Assert.False(deadline.Token.IsCancellationRequested);

        Awake(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void An_overrun_while_weir_was_awake_is_stopped_at_the_limit()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);

        Awake(TimeSpan.FromSeconds(59));
        Assert.False(deadline.Token.IsCancellationRequested);

        Awake(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_sleep_before_any_awake_time_has_passed_still_leaves_the_whole_limit()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);
        Sleeps(TimeSpan.FromHours(7));

        Awake(TimeSpan.FromSeconds(59));
        Assert.False(deadline.Token.IsCancellationRequested);

        Awake(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Two_sleeps_and_an_overrun_stop_at_the_awake_time_in_all()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);
        Awake(TimeSpan.FromSeconds(20));
        Sleeps(TimeSpan.FromMinutes(30));
        Awake(TimeSpan.FromSeconds(20));
        Sleeps(TimeSpan.FromHours(3));
        Awake(TimeSpan.FromSeconds(19));
        Assert.False(deadline.Token.IsCancellationRequested);

        Awake(TimeSpan.FromSeconds(1));

        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Without_a_pulse_the_limit_is_plain_elapsed_time()
    {
        using var deadline = new AwakeDeadline(Limit, _time, pulse: null);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(deadline.Token.IsCancellationRequested);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Restarting_the_limit_counts_from_then()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse);
        Awake(TimeSpan.FromSeconds(50));

        deadline.Restart();
        Awake(TimeSpan.FromSeconds(59));
        Assert.False(deadline.Token.IsCancellationRequested);

        Awake(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_limit_that_was_not_started_never_runs_out_until_it_is()
    {
        using var deadline = new AwakeDeadline(Limit, _time, _pulse, started: false);
        Awake(TimeSpan.FromMinutes(5));
        Assert.False(deadline.Token.IsCancellationRequested);

        deadline.Restart();
        Awake(TimeSpan.FromSeconds(60));

        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void No_limit_never_runs_out()
    {
        using var deadline = new AwakeDeadline(limit: null, _time, _pulse);

        Awake(TimeSpan.FromHours(1));
        deadline.Restart();
        Awake(TimeSpan.FromHours(1));

        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_limit_that_is_disposed_does_not_fire()
    {
        var deadline = new AwakeDeadline(Limit, _time, _pulse);
        var token = deadline.Token;

        deadline.Dispose();
        Awake(TimeSpan.FromMinutes(5));

        Assert.False(token.IsCancellationRequested);
    }
}
