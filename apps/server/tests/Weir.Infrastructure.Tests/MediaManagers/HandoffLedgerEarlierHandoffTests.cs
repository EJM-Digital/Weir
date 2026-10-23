using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Which records belong to an earlier hand-off of the same path: only those clearly older than this hand-off's arrival.</summary>
public sealed class HandoffLedgerEarlierHandoffTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static HandoffLedgerRow Row(DateTimeOffset? receivedAt) =>
        new(1, "radarr", "h-1", 1, "Film/Film.mkv", "queued", null, null, null, ReceivedAt: receivedAt);

    [Theory]
    [InlineData(-120, true)]
    [InlineData(-61, true)]
    [InlineData(-59, false)]
    [InlineData(0, false)]
    [InlineData(30, false)]
    public void A_record_is_from_an_earlier_hand_off_only_when_it_is_more_than_a_minute_older_than_the_arrival(int secondsFromArrival, bool earlier) =>
        Assert.Equal(earlier, HandoffLedgerStore.IsFromEarlierHandoff(Row(Received), Received.AddSeconds(secondsFromArrival)));

    [Fact]
    public void A_record_with_no_time_or_a_hand_off_with_no_arrival_is_never_from_an_earlier_one()
    {
        Assert.False(HandoffLedgerStore.IsFromEarlierHandoff(Row(Received), null));
        Assert.False(HandoffLedgerStore.IsFromEarlierHandoff(Row(null), Received.AddDays(-30)));
    }
}
