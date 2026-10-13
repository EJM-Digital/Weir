using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The count of failed starts: capped exponential backoff with jitter, the start limit (N failures within a window), and the
/// count starting over after a stable run. All on fake time, with the jitter's random number supplied, so nothing waits.
/// </summary>
public sealed class StartSupervisionTests
{
    private static readonly StartPolicy Policy = StartPolicy.Default;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));
    private double _random;

    private StartSupervision Supervision() => new(Policy, _time, () => _random);

    [Fact]
    public void The_policy_is_the_one_the_documentation_names()
    {
        // docs/start-supervision.md gives each of these with the measurement or the precedent behind it.
        Assert.Equal(TimeSpan.FromSeconds(30), Policy.FirstProgressWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), Policy.StallWindow);
        Assert.Equal(TimeSpan.FromHours(1), Policy.StallCeiling);
        Assert.Equal(TimeSpan.FromSeconds(2), Policy.InitialBackoff);
        Assert.Equal(2d, Policy.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(60), Policy.MaxBackoff);
        Assert.Equal(5, Policy.StartLimitBurst);
        Assert.Equal(TimeSpan.FromMinutes(15), Policy.StartLimitInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), Policy.StableAfter);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(7, 60)]
    [InlineData(40, 60)]
    public void The_backoff_doubles_and_stops_at_the_cap(int failuresInARow, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Policy.BackoffCeiling(failuresInARow));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]
    public void The_jittered_wait_is_between_half_the_ceiling_and_the_ceiling_whatever_the_random_number(int failuresInARow)
    {
        var ceiling = Policy.BackoffCeiling(failuresInARow);

        Assert.Equal(ceiling / 2, Policy.Backoff(failuresInARow, () => 0));
        Assert.Equal(ceiling, Policy.Backoff(failuresInARow, () => 1));
        foreach (var random in new[] { 0.0, 0.001, 0.25, 0.5, 0.75, 0.999 })
        {
            var wait = Policy.Backoff(failuresInARow, () => random);
            Assert.InRange(wait, ceiling / 2, ceiling);
        }
    }

    [Fact]
    public void A_random_number_outside_zero_to_one_cannot_push_the_wait_outside_its_bounds()
    {
        var ceiling = Policy.BackoffCeiling(3);

        Assert.Equal(ceiling / 2, Policy.Backoff(3, () => -4));
        Assert.Equal(ceiling, Policy.Backoff(3, () => 7));
    }

    [Fact]
    public void Two_runs_with_different_random_numbers_wait_differently_so_restarts_do_not_land_together()
    {
        Assert.NotEqual(Policy.Backoff(3, () => 0.1), Policy.Backoff(3, () => 0.9));
    }

    [Fact]
    public void A_failure_is_a_retry_after_the_jittered_wait_with_the_attempt_that_failed()
    {
        _random = 0;
        var supervision = Supervision();
        Assert.Equal(1, supervision.Attempt);

        var decision = supervision.Failed("exited with code 3");

        Assert.False(decision.GiveUp);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal("exited with code 3", decision.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
        Assert.Equal(1, decision.FailuresInWindow);
        Assert.Equal(2, supervision.Attempt);
    }

    [Fact]
    public void Each_further_failure_in_a_row_waits_longer()
    {
        _random = 1;
        var supervision = Supervision();

        var delays = new List<TimeSpan>();
        for (var failure = 1; failure < Policy.StartLimitBurst; failure++)
        {
            delays.Add(supervision.Failed("exited").Delay);
        }

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)], delays);
    }

    [Fact]
    public void The_start_limit_is_reached_at_the_burst_number_of_failures_and_no_further_attempt_follows()
    {
        var supervision = Supervision();
        StartDecision last = default;

        for (var failure = 1; failure <= Policy.StartLimitBurst; failure++)
        {
            last = supervision.Failed($"hung {failure}");
            Assert.Equal(failure == Policy.StartLimitBurst, last.GiveUp);
        }

        Assert.Equal(Policy.StartLimitBurst, last.Attempt);
        Assert.Equal(TimeSpan.Zero, last.Delay);
        Assert.Equal(Policy.StartLimitBurst, last.FailuresInWindow);
    }

    [Fact]
    public void Failures_spread_further_apart_than_the_window_never_reach_the_limit()
    {
        var supervision = Supervision();

        for (var failure = 0; failure < 40; failure++)
        {
            Assert.False(supervision.Failed("exited").GiveUp);
            _time.Advance(Policy.StartLimitInterval + TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void A_failure_falls_out_of_the_window_once_it_is_older_than_the_window()
    {
        var supervision = Supervision();
        for (var failure = 1; failure < Policy.StartLimitBurst; failure++)
        {
            Assert.False(supervision.Failed("exited").GiveUp);
        }

        _time.Advance(Policy.StartLimitInterval + TimeSpan.FromSeconds(1));
        var decision = supervision.Failed("exited");

        Assert.False(decision.GiveUp);
        Assert.Equal(1, decision.FailuresInWindow);
    }

    [Fact]
    public void A_stable_run_starts_the_count_over()
    {
        _random = 1;
        var supervision = Supervision();
        for (var failure = 1; failure < Policy.StartLimitBurst; failure++)
        {
            supervision.Failed("exited");
        }

        supervision.Ready();
        _time.Advance(Policy.StableAfter);
        var decision = supervision.Failed("exited with code 1");

        Assert.False(decision.GiveUp);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(1, decision.FailuresInWindow);
        Assert.Equal(Policy.InitialBackoff, decision.Delay);
        Assert.Equal(["attempt 1: exited with code 1"], supervision.Failures);
    }

    [Fact]
    public void A_run_that_was_ready_for_less_than_the_stable_time_does_not_start_the_count_over()
    {
        var supervision = Supervision();
        for (var failure = 1; failure < Policy.StartLimitBurst; failure++)
        {
            supervision.Failed("exited");
        }

        supervision.Ready();
        _time.Advance(Policy.StableAfter - TimeSpan.FromSeconds(1));
        var decision = supervision.Failed("exited");

        Assert.True(decision.GiveUp);
        Assert.Equal(Policy.StartLimitBurst, decision.Attempt);
    }

    [Fact]
    public void A_person_asking_for_a_new_start_gets_the_whole_budget_again()
    {
        var supervision = Supervision();
        for (var failure = 1; failure < Policy.StartLimitBurst; failure++)
        {
            supervision.Failed("exited");
        }

        supervision.Reset();

        Assert.Equal(1, supervision.Attempt);
        Assert.Empty(supervision.Failures);
        Assert.False(supervision.Failed("exited").GiveUp);
    }

    [Fact]
    public void The_failures_are_kept_in_order_with_their_attempt_numbers_for_the_log_and_for_the_next_server()
    {
        var supervision = Supervision();

        supervision.Failed("hung in phase 'opening the database' for 31 s");
        supervision.Failed("exited with code 1");

        Assert.Equal(
            ["attempt 1: hung in phase 'opening the database' for 31 s", "attempt 2: exited with code 1"],
            supervision.Failures);
        Assert.Equal(3, supervision.Attempt);
    }
}
