using Microsoft.Extensions.Time.Testing;
using Weir.Core.Auth;

namespace Weir.Core.Tests.Auth;

/// <summary>The per-account backoff when its history ages out, is full, or is asked about a missing name.</summary>
public sealed class UsernameLoginBackoffEdgeTests
{
    [Fact]
    public void A_failure_after_the_window_has_passed_starts_the_count_again()
    {
        var time = new FakeTimeProvider();
        var backoff = new UsernameLoginBackoff(time);
        FailPastTheFreeAttempts(backoff, "alice");
        Assert.True(backoff.TimeUntilAllowed("alice") > TimeSpan.Zero);

        time.Advance(TimeSpan.FromSeconds(UsernameLoginBackoff.WindowSeconds + 1));
        backoff.RecordFailure("alice");

        Assert.Equal(TimeSpan.Zero, backoff.TimeUntilAllowed("alice"));
    }

    [Fact]
    public void The_account_tried_longest_ago_is_forgotten_when_too_many_accounts_are_tracked()
    {
        var backoff = new UsernameLoginBackoff(new FakeTimeProvider());
        FailPastTheFreeAttempts(backoff, "first");
        Assert.True(backoff.TimeUntilAllowed("first") > TimeSpan.Zero);

        for (var i = 0; i < 10_000; i++)
        {
            backoff.RecordFailure($"user-{i}");
        }

        Assert.Equal(TimeSpan.Zero, backoff.TimeUntilAllowed("first"));
    }

    [Fact]
    public void A_missing_name_is_one_account_like_any_other()
    {
        var backoff = new UsernameLoginBackoff(new FakeTimeProvider());

        backoff.RecordFailure(null!);

        Assert.Equal(TimeSpan.Zero, backoff.TimeUntilAllowed(string.Empty));
    }

    private static void FailPastTheFreeAttempts(UsernameLoginBackoff backoff, string username)
    {
        for (var i = 0; i <= UsernameLoginBackoff.FreeAttempts; i++)
        {
            backoff.RecordFailure(username);
        }
    }
}
