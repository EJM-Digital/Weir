using Weir.Core.Auth;

namespace Weir.Core.Tests.Auth;

/// <summary>The wording of what a recovery tells the Activity page and the alerts, counted the way every other message is.</summary>
public sealed class RecoveryCodeMessagesTests
{
    [Theory]
    [InlineData(9, "alice. 9 of 10 recovery codes left.")]
    [InlineData(1, "alice. 1 of 10 recovery codes left.")]
    [InlineData(0, "alice. 0 of 10 recovery codes left.")]
    public void The_activity_detail_counts_the_codes_that_are_left_out_of_a_set(int left, string expected)
    {
        Assert.Equal(expected, RecoveryCodeMessages.ActivityDetail("alice", left));
    }
}
