using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The pulse notes the time on a thread of its own once a second, so a silence of <see cref="ProcessPulse.AwayAfter"/> or more in
/// its notes means nothing in Weir ran: the computer slept, the machine was saved or paused, or the process was frozen. A scheduler
/// that merely fell behind leaves the notes unbroken. Time is the fake clock's.
/// </summary>
public sealed class ProcessPulseTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private void RunsFor(ProcessPulse pulse, TimeSpan time)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += OneSecond)
        {
            _time.Advance(OneSecond);
            pulse.Note();
        }
    }

    [Fact]
    public void A_process_that_ran_all_along_was_never_away()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);

        RunsFor(pulse, TimeSpan.FromMinutes(30));

        Assert.Equal(new Absence(TimeSpan.Zero, TimeSpan.Zero), pulse.AwayWithin(start, _time.GetUtcNow()));
    }

    [Fact]
    public void A_silence_just_under_the_limit_is_a_busy_machine_not_an_absence()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        RunsFor(pulse, TimeSpan.FromSeconds(5));

        _time.Advance(ProcessPulse.AwayAfter - TimeSpan.FromMilliseconds(1));
        pulse.Note();

        Assert.Equal(Absence.None, pulse.AwayWithin(start, _time.GetUtcNow()));
    }

    [Fact]
    public void A_silence_of_the_limit_or_more_is_an_absence_once_the_process_wakes_and_notes_it()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        RunsFor(pulse, TimeSpan.FromSeconds(5));

        _time.Advance(TimeSpan.FromHours(3));
        pulse.Note();

        Assert.Equal(new Absence(TimeSpan.FromHours(3), TimeSpan.FromHours(3)), pulse.AwayWithin(start, _time.GetUtcNow()));
    }

    [Fact]
    public void A_silence_nobody_has_noted_yet_still_counts_when_asked_after_waking()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        RunsFor(pulse, TimeSpan.FromSeconds(5));

        _time.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(new Absence(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20)), pulse.AwayWithin(start, _time.GetUtcNow()));
    }

    [Fact]
    public void A_stretch_is_told_only_what_overlaps_it_and_the_whole_length_of_the_silence_that_reaches_into_it()
    {
        var pulse = new ProcessPulse(_time);
        RunsFor(pulse, TimeSpan.FromSeconds(5));
        var slept = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromHours(2));
        pulse.Note();

        var absence = pulse.AwayWithin(slept + TimeSpan.FromHours(1), slept + TimeSpan.FromHours(5));

        Assert.Equal(new Absence(TimeSpan.FromHours(1), TimeSpan.FromHours(2)), absence);
    }

    [Fact]
    public void A_stretch_that_ended_before_the_silence_began_is_not_told_of_it()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        RunsFor(pulse, TimeSpan.FromMinutes(1));
        var before = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromHours(2));
        pulse.Note();

        Assert.Equal(Absence.None, pulse.AwayWithin(start, before));
    }

    [Fact]
    public void Two_silences_add_up()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        _time.Advance(TimeSpan.FromMinutes(1));
        pulse.Note();
        RunsFor(pulse, TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromMinutes(2));
        pulse.Note();

        Assert.Equal(new Absence(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3)), pulse.AwayWithin(start, _time.GetUtcNow()));
    }

    [Fact]
    public void A_clock_set_back_is_not_an_absence()
    {
        var clock = new SettableClock(_time.GetUtcNow());
        var start = clock.GetUtcNow();
        var pulse = new ProcessPulse(clock);
        clock.Now = start.AddSeconds(5);
        pulse.Note();

        clock.Now = start - TimeSpan.FromHours(1);
        pulse.Note();
        clock.Now += TimeSpan.FromSeconds(5);
        pulse.Note();

        Assert.Equal(Absence.None, pulse.AwayWithin(start - TimeSpan.FromHours(2), clock.GetUtcNow()));
    }

    [Fact]
    public void Only_the_most_recent_silences_are_kept()
    {
        var start = _time.GetUtcNow();
        var pulse = new ProcessPulse(_time);
        for (var absence = 0; absence < 40; absence++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            pulse.Note();
        }

        Assert.Equal(TimeSpan.FromMinutes(32), pulse.AwayWithin(start, _time.GetUtcNow()).Within);
    }

    [Fact]
    public async Task The_service_notes_the_time_on_its_own_until_it_is_stopped()
    {
        using var pulse = new ProcessPulse(TimeProvider.System, TimeSpan.FromMilliseconds(5));

        await pulse.StartAsync(CancellationToken.None);
        await WaitUntil(() => pulse.Notes >= 3);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pulse.StopAsync(ceiling.Token);

        Assert.True(pulse.Notes >= 3);
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), ceiling.Token);
        }
    }
}
