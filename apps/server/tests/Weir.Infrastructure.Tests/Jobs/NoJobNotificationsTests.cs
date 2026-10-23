using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>The notifier used when no sender is registered: a job that ends is not made to wait on one.</summary>
public sealed class NoJobNotificationsTests
{
    [Fact]
    public void Nothing_is_sent_and_nothing_is_thrown_for_a_job_that_ended_either_way()
    {
        var none = new NoJobNotifications();

        Assert.Null(Record.Exception(() => none.Dispatch("processing", "completed", 1, "processing.file.remux_pass.v1")));
        Assert.Null(Record.Exception(() => none.Dispatch("processing", "failed", 2, "processing.file.remux_pass.v1", willRetry: true)));
        Assert.Null(Record.Exception(() => none.DispatchFailedForGood("processing", 3, "processing.file.remux_pass.v1")));
    }
}
