using Weir.Core.Media;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Media;

/// <summary>Progress figures that cannot be turned into whole seconds are refused with the reason, not guessed.</summary>
public sealed class FfmpegProgressTrackerEdgeTests
{
    private static RulesInputException Refused(double duration, string outTimeMs, double secondsSinceStart)
    {
        var tracker = new FfmpegProgressTracker(duration, timeoutSeconds: null);
        tracker.Feed($"out_time_ms={outTimeMs}", 0);
        return Assert.Throws<RulesInputException>(() => tracker.Feed("progress=continue", secondsSinceStart));
    }

    [Fact]
    public void A_run_that_has_lasted_forever_has_no_time_left_to_work_out()
    {
        var error = Refused(100, "50000000", double.PositiveInfinity);

        Assert.Equal(("ValueError", "An ffmpeg progress time is not a number."), (error.ErrorKind, error.Message));
    }

    [Fact]
    public void A_projection_beyond_what_a_number_can_hold_is_refused_as_infinite()
    {
        var error = Refused(1e308, "1000000", 1e5);

        Assert.Equal(("OverflowError", "An ffmpeg progress time is infinite."), (error.ErrorKind, error.Message));
    }

    [Fact]
    public void A_projection_beyond_what_a_long_can_hold_is_refused_as_too_large()
    {
        var error = Refused(1e12, "1000000", 1e10);

        Assert.Equal("OverflowError", error.ErrorKind);
        Assert.StartsWith("The ffmpeg progress time ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" is too large to use.", error.Message, StringComparison.Ordinal);
    }
}
