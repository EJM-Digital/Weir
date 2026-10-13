using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What counts as a hung start: no progress for as long as the phase is allowed, judged on the tray's own clock. A slow start that
/// keeps reporting is never hung; a stale, missing or foreign progress record is no progress; and a record is progress because it
/// is different from the one before it, never because of what its time says. Fake time throughout.
/// </summary>
public sealed class StartWatchTests
{
    private const int ServerPid = 5764;
    private static readonly StartPolicy Policy = StartPolicy.Default;
    private static readonly DateTimeOffset Noon = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));

    private StartWatch Watch() => new(Policy, _time, ServerPid);

    private static StartProgress Reading(string phase = "starting", int step = 0, int steps = 0, int expectSeconds = 0, int pid = ServerPid, TimeSpan? at = null) =>
        new(pid, phase, step, steps, TimeSpan.FromSeconds(expectSeconds), Noon + (at ?? TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void A_server_that_has_reported_nothing_is_hung_only_after_the_first_progress_window()
    {
        var watch = Watch();

        _time.Advance(Policy.FirstProgressWindow - TimeSpan.FromSeconds(1));
        Assert.False(watch.Check(null, null).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(null, null);

        Assert.True(verdict.Hung);
        Assert.Equal("before it reported anything", verdict.Where);
        Assert.Equal(Policy.FirstProgressWindow, verdict.SilentFor);
        Assert.Equal(Policy.FirstProgressWindow, verdict.Allowed);
    }

    [Fact]
    public void A_server_that_reported_and_then_went_quiet_is_hung_after_the_stall_window_in_the_phase_it_was_last_in()
    {
        var watch = Watch();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(watch.Check(Reading("opening_database"), null).Hung);

        _time.Advance(Policy.StallWindow - TimeSpan.FromSeconds(1));
        Assert.False(watch.Check(Reading("opening_database"), null).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(Reading("opening_database"), null);

        Assert.True(verdict.Hung);
        Assert.Equal("opening the database", verdict.Where);
        Assert.Equal(Policy.StallWindow, verdict.SilentFor);
    }

    [Fact]
    public void A_slow_start_that_keeps_reporting_is_never_hung_however_long_it_takes()
    {
        var watch = Watch();

        // Two hours of a migration that moves on every twenty seconds: far past every window, never silent for one.
        for (var report = 1; report <= 360; report++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            var verdict = watch.Check(Reading("migrating", step: 1 + (report % 7), steps: 7, at: TimeSpan.FromSeconds(20 * report)), null);
            Assert.False(verdict.Hung, $"Declared hung at report {report}");
        }
    }

    [Fact]
    public void A_beat_in_the_same_phase_counts_as_progress_because_the_record_changed()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", at: TimeSpan.FromSeconds(1)), null);

        for (var beat = 2; beat < 100; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            Assert.False(watch.Check(Reading("saving_copy", at: TimeSpan.FromSeconds(beat)), null).Hung);
        }
    }

    [Fact]
    public void The_same_record_read_again_is_no_progress()
    {
        var watch = Watch();
        var record = Reading("saving_copy");
        watch.Check(record, null);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(record, null).Hung);
    }

    [Fact]
    public void Progress_on_either_channel_is_progress()
    {
        var watch = Watch();
        watch.Check(Reading("opening_database", at: TimeSpan.FromSeconds(1)), Reading("opening_database", at: TimeSpan.FromSeconds(1)));

        // The file stops changing (refused writes); the pipe goes on.
        for (var beat = 2; beat < 60; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.False(watch.Check(Reading("saving_copy", at: TimeSpan.FromSeconds(beat)), Reading("opening_database", at: TimeSpan.FromSeconds(1))).Hung);
        }

        // And the other way round: the pipe is silent and the file moves.
        for (var beat = 2; beat < 60; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.False(watch.Check(Reading("saving_copy", at: TimeSpan.FromSeconds(59)), Reading("migrating", step: beat, steps: 99, at: TimeSpan.FromSeconds(beat))).Hung);
        }
    }

    [Fact]
    public void Two_channels_that_each_stay_the_same_are_silence_however_they_are_asked()
    {
        var watch = Watch();
        var pipe = Reading("saving_copy", at: TimeSpan.FromSeconds(5));
        var file = Reading("opening_database", at: TimeSpan.FromSeconds(1));
        watch.Check(pipe, file);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(pipe, file).Hung);
        Assert.True(watch.Check(null, file).Hung);
        Assert.True(watch.Check(pipe, null).Hung);
    }

    [Fact]
    public void A_server_that_asks_for_longer_silence_is_given_it()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", expectSeconds: 468), null);

        _time.Advance(TimeSpan.FromSeconds(467));
        Assert.False(watch.Check(Reading("saving_copy", expectSeconds: 468), null).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(Reading("saving_copy", expectSeconds: 468), null);

        Assert.True(verdict.Hung);
        Assert.Equal(TimeSpan.FromSeconds(468), verdict.Allowed);
    }

    [Fact]
    public void A_request_for_less_than_the_stall_window_does_not_shorten_it()
    {
        var watch = Watch();
        watch.Check(Reading("opening_database", expectSeconds: 3), null);

        _time.Advance(TimeSpan.FromSeconds(29));

        Assert.False(watch.Check(Reading("opening_database", expectSeconds: 3), null).Hung);
    }

    [Fact]
    public void A_request_past_the_ceiling_is_believed_only_up_to_the_ceiling()
    {
        var watch = Watch();
        watch.Check(Reading("migrating", step: 1, steps: 2, expectSeconds: 100_000), null);

        _time.Advance(Policy.StallCeiling);
        var verdict = watch.Check(Reading("migrating", step: 1, steps: 2, expectSeconds: 100_000), null);

        Assert.True(verdict.Hung);
        Assert.Equal(Policy.StallCeiling, verdict.Allowed);
    }

    [Fact]
    public void A_new_phase_without_a_request_goes_back_to_the_stall_window()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", expectSeconds: 468), null);
        _time.Advance(TimeSpan.FromSeconds(100));
        watch.Check(Reading("starting_listener", at: TimeSpan.FromSeconds(100)), null);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(Reading("starting_listener", at: TimeSpan.FromSeconds(100)), null).Hung);
    }

    [Fact]
    public void A_record_of_a_different_process_is_ignored_on_either_channel()
    {
        var watch = Watch();

        // The last start's file, left behind, and a line from some other process.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(watch.Check(Reading("ready", pid: 11), Reading("ready", pid: 12)).Hung);
        Assert.Null(watch.Last);

        _time.Advance(Policy.FirstProgressWindow);
        var verdict = watch.Check(Reading("ready", pid: 11), Reading("ready", pid: 12));

        Assert.True(verdict.Hung);
        Assert.Equal("before it reported anything", verdict.Where);
    }

    [Fact]
    public void A_missing_reading_after_the_server_reported_is_silence_not_a_new_start()
    {
        var watch = Watch();
        watch.Check(Reading("opening_database"), null);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(null, null).Hung);
    }

    [Fact]
    public void A_record_stamped_a_day_ahead_is_progress_only_when_it_changes()
    {
        var watch = Watch();
        _time.Advance(TimeSpan.FromSeconds(10));
        watch.Check(Reading("opening_database", at: TimeSpan.FromDays(1)), null);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(watch.Check(Reading("saving_copy", at: TimeSpan.FromDays(1)), null).Hung);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(Reading("saving_copy", at: TimeSpan.FromDays(1)), null).Hung);
    }

    [Fact]
    public void A_clock_that_steps_back_in_the_middle_of_a_start_does_not_make_good_records_look_stale()
    {
        var watch = Watch();

        // A virtual machine's time sync, or a snapshot resumed, puts the server's clock hours behind where it was: every record from
        // then on is stamped earlier than the ones before it, and each of them is still progress.
        for (var beat = 0; beat < 100; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            var stamp = beat < 50 ? TimeSpan.FromSeconds(beat) : TimeSpan.FromHours(-3) + TimeSpan.FromSeconds(beat);
            Assert.False(watch.Check(Reading("saving_copy", at: stamp), null).Hung, $"Declared hung at beat {beat}");
        }
    }

    [Fact]
    public void A_clock_that_steps_back_does_not_make_a_record_a_new_server_would_write_look_older_than_the_process()
    {
        var watch = Watch();

        // The first record this server writes is stamped long before any time the tray remembers: it is still this server's.
        var verdict = watch.Check(Reading("starting", at: TimeSpan.FromDays(-30)), null);

        Assert.False(verdict.Hung);
        Assert.Equal("starting", watch.Last?.Phase);
    }
}
