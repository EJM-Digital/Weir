using Weir.Core.Media;

namespace Weir.Core.Tests.Media;

/// <summary>
/// Reading what ffmpeg reports about hardware acceleration. Detection against a fake runner lives in
/// <c>Weir.Infrastructure.Tests</c>; here, reading ffmpeg's answer.
/// </summary>
public sealed class HardwareAccelerationTests
{
    // --- detection -----------------------------------------------------------------------

    [Fact]
    public void Ffmpegs_reported_methods_are_parsed()
    {
        var report = HardwareAcceleration.ReportFromHwaccels(0, "Hardware acceleration methods:\ncuda\nvaapi\nqsv\n");

        Assert.True(report.Detected);
        Assert.Equal(["cuda", "qsv", "vaapi"], report.AvailableMethods.Order());
        Assert.Equal(["intel", "nvidia", "vaapi"], report.Vendors.Order());
        Assert.Contains("does not prove a device is present", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_with_no_acceleration_reports_none_and_says_so()
    {
        var report = HardwareAcceleration.ReportFromHwaccels(0, "Hardware acceleration methods:\n");

        Assert.True(report.Detected);
        Assert.Empty(report.AvailableMethods);
        Assert.Contains("no hardware acceleration methods", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_ffmpeg_reports_nothing_rather_than_raising()
    {
        var report = HardwareAcceleration.ReportForRunError("no such file");

        Assert.False(report.Detected);
        Assert.Empty(report.AvailableMethods);
        Assert.Contains("could not ask ffmpeg", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slow_ffmpeg_says_it_did_not_answer_in_time_and_does_not_blame_the_ask()
    {
        var report = HardwareAcceleration.ReportForTimeout();

        Assert.False(report.Detected);
        Assert.Empty(report.AvailableMethods);
        Assert.Equal("FFmpeg didn't answer in time. Check again in a moment.", report.Detail);
        Assert.DoesNotContain("could not ask", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_ffmpeg_reports_nothing_rather_than_raising()
    {
        var report = HardwareAcceleration.ReportFromHwaccels(1, string.Empty);

        Assert.False(report.Detected);
        Assert.Contains("software decoding", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_selectable_vendor_maps_to_at_least_one_method()
    {
        foreach (var (vendor, methods) in HardwareAcceleration.VendorMethods)
        {
            Assert.True(methods.Count > 0, vendor);
        }
    }
}
