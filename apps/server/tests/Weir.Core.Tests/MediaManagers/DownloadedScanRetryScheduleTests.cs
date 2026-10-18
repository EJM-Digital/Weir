using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>When Weir asks a Sonarr or Radarr to scan again after it said it was busy with another scan.</summary>
public sealed class DownloadedScanRetryScheduleTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(5, 240)]
    public void The_wait_grows_with_each_busy_answer(int busyAnswers, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), DownloadedScanRetrySchedule.WaitAfter(busyAnswers, retryAfter: null));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void After_the_last_wait_Weir_stops_asking(int busyAnswers)
    {
        Assert.Null(DownloadedScanRetrySchedule.WaitAfter(busyAnswers, retryAfter: TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_count_below_one_has_no_wait(int busyAnswers)
    {
        Assert.Null(DownloadedScanRetrySchedule.WaitAfter(busyAnswers, retryAfter: null));
    }

    [Theory]
    [InlineData(1, 5, 15)]
    [InlineData(1, 45, 45)]
    [InlineData(3, 61, 61)]
    [InlineData(1, 99999, 240)]
    public void A_Retry_After_lengthens_the_wait_but_never_past_the_longest_and_never_shortens_it(int busyAnswers, int retryAfterSeconds, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), DownloadedScanRetrySchedule.WaitAfter(busyAnswers, TimeSpan.FromSeconds(retryAfterSeconds)));
    }

    [Fact]
    public void Weir_asks_again_as_many_times_as_it_has_waits_and_the_longest_is_the_last()
    {
        Assert.Equal(5, DownloadedScanRetrySchedule.MostAsksAgain);
        Assert.Equal(TimeSpan.FromMinutes(4), DownloadedScanRetrySchedule.LongestWait);
        Assert.Equal(DownloadedScanRetrySchedule.Waits.Order(), DownloadedScanRetrySchedule.Waits);
    }
}
