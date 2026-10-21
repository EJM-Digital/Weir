using Weir.Core.Time;

namespace Weir.Core.Tests.Time;

/// <summary>Zone names that are unknown or malformed, and wall-clock times a daylight-saving change makes ambiguous.</summary>
public sealed class TimeZonesEdgeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Eastern Standard Time")]
    [InlineData("America/New_York\0")]
    [InlineData("america/new_york")]
    public void A_name_that_is_not_an_exact_iana_name_is_not_found(string? name)
    {
        Assert.False(TimeZones.TryFind(name, out var zone));
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Fact]
    public void An_exact_iana_name_is_found()
    {
        Assert.True(TimeZones.TryFind("Europe/London", out var zone));
        Assert.Equal(TimeSpan.Zero, zone.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UTC")]
    [InlineData("Not/AZone")]
    public void A_missing_or_unknown_zone_means_utc(string? name) =>
        Assert.Equal(TimeZoneInfo.Utc, TimeZones.Find(name));

    [Fact]
    public void A_time_that_happens_twice_takes_the_offset_before_the_change()
    {
        var zone = TimeZones.Find("America/New_York");

        // 01:30 on 1 November 2026 is both daylight (-04:00) and standard (-05:00) time; the first is the one in force before the change.
        var instant = TimeZones.FromWallClock(new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified), zone);

        Assert.Equal(TimeSpan.FromHours(-4), instant.Offset);
    }

    [Fact]
    public void A_time_that_never_happens_takes_the_offset_before_the_change()
    {
        var zone = TimeZones.Find("America/New_York");

        // 02:30 on 8 March 2026 is skipped when the clocks go forward.
        var instant = TimeZones.FromWallClock(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified), zone);

        Assert.Equal(TimeSpan.FromHours(-5), instant.Offset);
    }
}
