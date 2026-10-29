using Weir.Core.Auth;

namespace Weir.Core.Tests.Auth;

/// <summary>The wording of what a recovery tells the Activity page and the alerts, counted the way every other message is.</summary>
public sealed class RecoveryCodeMessagesTests
{
    [Theory]
    [InlineData(9, "alice. 9 recovery codes left.")]
    [InlineData(1, "alice. 1 recovery code left.")]
    [InlineData(0, "alice. 0 recovery codes left.")]
    public void The_activity_line_counts_the_codes_that_are_left(int left, string expected)
    {
        Assert.Equal(expected, RecoveryCodeMessages.ActivityDetail("alice", left));
    }

    [Fact]
    public void The_alert_says_who_got_back_in_how_many_codes_are_left_and_what_to_do_if_it_was_not_them()
    {
        var detail = RecoveryCodeMessages.AlertDetail("alice", 8);

        Assert.Contains("as alice", detail, StringComparison.Ordinal);
        Assert.Contains("8 recovery codes left", detail, StringComparison.Ordinal);
        Assert.Contains("make a new set of recovery codes", detail, StringComparison.Ordinal);
    }
}
