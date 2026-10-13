using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What counts as a hung start: no progress for as long as the phase is allowed, judged on the tray's own clock. A slow start that
/// keeps reporting is never hung; a stale, missing or foreign progress file is no progress. Fake time throughout.
/// </summary>
public sealed class StartWatchTests
{
    private const int ServerPid = 5764;
    private static readonly StartPolicy Policy = StartPolicy.Default;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _processStarted;

    public StartWatchTests()
    {
        _processStarted = _time.GetUtcNow();
    }

    private StartWatch Watch() => new(Policy, _time, ServerPid, _processStarted);

    private StartProgress Reading(string phase = "starting", int step = 0, int steps = 0, int expectSeconds = 0, int pid = ServerPid, TimeSpan? writtenAfterStart = null) =>
        new(pid, phase, step, steps, TimeSpan.FromSeconds(expectSeconds), _processStarted + (writtenAfterStart ?? TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void A_server_that_has_reported_nothing_is_hung_only_after_the_first_progress_window()
    {
        var watch = Watch();

        _time.Advance(Policy.FirstProgressWindow - TimeSpan.FromSeconds(1));
        Assert.False(watch.Check(null).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(null);

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
        Assert.False(watch.Check(Reading("opening_database")).Hung);

        _time.Advance(Policy.StallWindow - TimeSpan.FromSeconds(1));
        Assert.False(watch.Check(Reading("opening_database")).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(Reading("opening_database"));

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
            var verdict = watch.Check(Reading("migrating", step: 1 + (report % 7), steps: 7, writtenAfterStart: TimeSpan.FromSeconds(20 * report)));
            Assert.False(verdict.Hung, $"Declared hung at report {report}");
        }
    }

    [Fact]
    public void A_beat_in_the_same_phase_counts_as_progress_because_the_record_changed()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", writtenAfterStart: TimeSpan.FromSeconds(1)));

        for (var beat = 2; beat < 100; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            Assert.False(watch.Check(Reading("saving_copy", writtenAfterStart: TimeSpan.FromSeconds(beat))).Hung);
        }
    }

    [Fact]
    public void The_same_record_read_again_is_no_progress()
    {
        var watch = Watch();
        var record = Reading("saving_copy");
        watch.Check(record);

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(record).Hung);
    }

    [Fact]
    public void A_server_that_asks_for_longer_silence_is_given_it()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", expectSeconds: 468));

        _time.Advance(TimeSpan.FromSeconds(467));
        Assert.False(watch.Check(Reading("saving_copy", expectSeconds: 468)).Hung);

        _time.Advance(TimeSpan.FromSeconds(1));
        var verdict = watch.Check(Reading("saving_copy", expectSeconds: 468));

        Assert.True(verdict.Hung);
        Assert.Equal(TimeSpan.FromSeconds(468), verdict.Allowed);
    }

    [Fact]
    public void A_request_for_less_than_the_stall_window_does_not_shorten_it()
    {
        var watch = Watch();
        watch.Check(Reading("opening_database", expectSeconds: 3));

        _time.Advance(TimeSpan.FromSeconds(29));

        Assert.False(watch.Check(Reading("opening_database", expectSeconds: 3)).Hung);
    }

    [Fact]
    public void A_request_past_the_ceiling_is_believed_only_up_to_the_ceiling()
    {
        var watch = Watch();
        watch.Check(Reading("migrating", step: 1, steps: 2, expectSeconds: 100_000));

        _time.Advance(Policy.StallCeiling);
        var verdict = watch.Check(Reading("migrating", step: 1, steps: 2, expectSeconds: 100_000));

        Assert.True(verdict.Hung);
        Assert.Equal(Policy.StallCeiling, verdict.Allowed);
    }

    [Fact]
    public void A_new_phase_without_a_request_goes_back_to_the_stall_window()
    {
        var watch = Watch();
        watch.Check(Reading("saving_copy", expectSeconds: 468));
        _time.Advance(TimeSpan.FromSeconds(100));
        watch.Check(Reading("starting_listener", writtenAfterStart: TimeSpan.FromSeconds(100)));

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(Reading("starting_listener", writtenAfterStart: TimeSpan.FromSeconds(100))).Hung);
    }

    [Fact]
    public void A_progress_file_of_a_different_process_is_ignored_so_the_server_still_counts_as_not_having_reported()
    {
        var watch = Watch();

        // The last start's file, left behind, written after this process began as far as the clock goes.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(watch.Check(Reading("ready", pid: 11)).Hung);
        Assert.Null(watch.Last);

        _time.Advance(Policy.FirstProgressWindow);
        var verdict = watch.Check(Reading("ready", pid: 11));

        Assert.True(verdict.Hung);
        Assert.Equal("before it reported anything", verdict.Where);
    }

    [Fact]
    public void A_progress_file_written_before_the_process_began_is_ignored_even_with_its_process_id()
    {
        var watch = Watch();
        var earlier = new StartProgress(ServerPid, "ready", 0, 0, TimeSpan.Zero, _processStarted - TimeSpan.FromHours(3));

        _time.Advance(Policy.FirstProgressWindow);
        var verdict = watch.Check(earlier);

        Assert.Null(watch.Last);
        Assert.True(verdict.Hung);
    }

    [Fact]
    public void A_missing_file_after_the_server_reported_is_silence_not_a_new_start()
    {
        var watch = Watch();
        watch.Check(Reading("opening_database"));

        _time.Advance(Policy.StallWindow);

        Assert.True(watch.Check(null).Hung);
    }

    [Fact]
    public void The_clock_of_the_server_is_never_compared_with_the_trays()
    {
        var watch = Watch();

        // A server whose clock reads a day ahead, then a day behind its own start: only the record changing counts.
        _time.Advance(TimeSpan.FromSeconds(10));
        watch.Check(Reading("opening_database", writtenAfterStart: TimeSpan.FromDays(1)));
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(watch.Check(Reading("saving_copy", writtenAfterStart: TimeSpan.FromDays(1))).Hung);
    }
}
