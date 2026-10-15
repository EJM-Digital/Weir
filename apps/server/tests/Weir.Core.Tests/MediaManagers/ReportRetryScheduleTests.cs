using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>When a report a manager could not take yet is sent again: a heartbeat later, doubling to an hour, and given up after a week.</summary>
public sealed class ReportRetryScheduleTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(8, 60)]
    [InlineData(40, 60)]
    [InlineData(int.MaxValue, 60)]
    public void The_wait_starts_at_a_minute_and_doubles_to_an_hour(int refusals, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), ReportRetrySchedule.Wait(refusals, retryAfter: null));
    }

    [Theory]
    [InlineData(1, 30, 1)]
    [InlineData(1, 120, 2)]
    [InlineData(3, 120, 4)]
    [InlineData(3, 600, 10)]
    [InlineData(1, 99999, 60)]
    public void A_Retry_After_lengthens_the_wait_but_never_past_an_hour_and_never_shortens_it(int refusals, int retryAfterSeconds, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), ReportRetrySchedule.Wait(refusals, TimeSpan.FromSeconds(retryAfterSeconds)));
    }

    [Fact]
    public void A_report_is_given_up_once_it_has_been_refused_for_a_week()
    {
        var first = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        Assert.False(ReportRetrySchedule.GivesUp(first, first.AddDays(7).AddTicks(-1)));
        Assert.True(ReportRetrySchedule.GivesUp(first, first.AddDays(7)));
        Assert.True(ReportRetrySchedule.GivesUp(first, first.AddDays(30)));
    }
}
