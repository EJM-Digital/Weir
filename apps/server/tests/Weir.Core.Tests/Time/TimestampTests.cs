using Microsoft.Extensions.Time.Testing;
using Weir.Core.Time;

namespace Weir.Core.Tests.Time;

/// <summary>The date-time values the database and API carry: how they are made, written and read back.</summary>
public sealed class TimestampTests
{
    private static readonly DateTime Clock = new(2026, 10, 19, 12, 30, 45, DateTimeKind.Unspecified);

    [Fact]
    public void The_current_instant_is_an_aware_utc_value_and_a_clock_is_required()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 30, 45, TimeSpan.FromHours(10)));

        var now = Timestamp.UtcNow(time);

        Assert.Equal(new DateTime(2026, 10, 19, 2, 30, 45), now.Clock);
        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.True(now.IsAware);
        Assert.Throws<ArgumentNullException>(() => Timestamp.UtcNow(null!));
    }

    [Fact]
    public void A_naive_value_has_no_offset_and_keeps_whole_microseconds()
    {
        var naive = Timestamp.Naive(DateTime.SpecifyKind(Clock.AddTicks(1239), DateTimeKind.Local));

        Assert.False(naive.IsAware);
        Assert.Null(naive.Offset);
        Assert.Equal(DateTimeKind.Unspecified, naive.Clock.Kind);
        Assert.Equal(Clock.AddTicks(1230), naive.Clock);
    }

    [Fact]
    public void Truncation_drops_ticks_below_a_microsecond_and_keeps_the_kind_or_offset()
    {
        var utc = new DateTime(Clock.Ticks + 7, DateTimeKind.Utc);
        var offset = new DateTimeOffset(Clock.Ticks + 9, TimeSpan.FromHours(-3));

        Assert.Equal(Clock.Ticks, Timestamp.TruncateToMicroseconds(utc).Ticks);
        Assert.Equal(DateTimeKind.Utc, Timestamp.TruncateToMicroseconds(utc).Kind);
        Assert.Equal(Clock.Ticks, Timestamp.TruncateToMicroseconds(offset).Ticks);
        Assert.Equal(TimeSpan.FromHours(-3), Timestamp.TruncateToMicroseconds(offset).Offset);
    }

    [Fact]
    public void An_offset_date_time_keeps_its_wall_clock_and_offset()
    {
        var value = Timestamp.FromDateTimeOffset(new DateTimeOffset(Clock.AddTicks(5), TimeSpan.FromHours(10)));

        Assert.Equal(Clock, value.Clock);
        Assert.Equal(TimeSpan.FromHours(10), value.Offset);
    }

    [Fact]
    public void The_instant_applies_the_offset_and_treats_a_naive_value_as_utc()
    {
        var aware = new Timestamp(Clock, TimeSpan.FromHours(10));
        var naive = new Timestamp(Clock, null);

        Assert.Equal(new DateTime(2026, 10, 19, 2, 30, 45, DateTimeKind.Utc), aware.AsUtc);
        Assert.Equal(new DateTime(2026, 10, 19, 12, 30, 45, DateTimeKind.Utc), naive.AsUtc);
        Assert.Equal(DateTimeKind.Utc, aware.AsUtc.Kind);
    }

    [Fact]
    public void The_database_text_is_the_wall_clock_to_the_microsecond_without_an_offset()
    {
        Assert.Equal("2026-10-19 12:30:45.000000", new Timestamp(Clock, TimeSpan.FromHours(10)).ToSqlite());
        Assert.Equal("2026-10-19 12:30:45.123456", Timestamp.Naive(Clock.AddTicks(1_234_567)).ToSqlite());
    }

    [Theory]
    [InlineData(0, null, "2026-10-19T12:30:45", "2026-10-19T12:30:45")]
    [InlineData(250_000, null, "2026-10-19T12:30:45.250000", "2026-10-19T12:30:45.250000")]
    [InlineData(1, 0, "2026-10-19T12:30:45.000001+00:00", "2026-10-19T12:30:45.000001Z")]
    [InlineData(0, 600, "2026-10-19T12:30:45+10:00", "2026-10-19T12:30:45+10:00")]
    [InlineData(0, -210, "2026-10-19T12:30:45-03:30", "2026-10-19T12:30:45-03:30")]
    public void Iso_text_writes_microseconds_only_when_present_and_the_wire_form_writes_a_zero_offset_as_z(
        int microseconds,
        int? offsetMinutes,
        string iso,
        string wire)
    {
        var value = new Timestamp(Clock.AddTicks(microseconds * 10L), offsetMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null);

        Assert.Equal(iso, value.IsoFormat());
        Assert.Equal(wire, value.ToWireText());
    }

    [Fact]
    public void Iso_text_takes_the_separator_it_is_given()
    {
        Assert.Equal("2026-10-19 12:30:45+01:00", new Timestamp(Clock, TimeSpan.FromHours(1)).IsoFormat(' '));
    }

    [Fact]
    public void An_offset_with_seconds_or_microseconds_writes_them_after_the_minutes()
    {
        var seconds = new Timestamp(Clock, new TimeSpan(5, 30, 15));
        var micro = new Timestamp(Clock, new TimeSpan(5, 30, 15).Add(TimeSpan.FromTicks(1230)));
        var negative = new Timestamp(Clock, -new TimeSpan(0, 4, 5, 6));
        var wholeSecondFraction = new Timestamp(Clock, TimeSpan.FromTicks(30));

        Assert.Equal("2026-10-19T12:30:45+05:30:15", seconds.IsoFormat());
        Assert.Equal("2026-10-19T12:30:45+05:30:15.000123", micro.IsoFormat());
        Assert.Equal("2026-10-19T12:30:45-04:05:06", negative.ToWireText());
        Assert.Equal("2026-10-19T12:30:45+00:00:00.000003", wholeSecondFraction.IsoFormat());
    }

    [Theory]
    [InlineData("2026-10-19", "2026-10-19 00:00:00.000000", null)]
    [InlineData("20261019", "2026-10-19 00:00:00.000000", null)]
    [InlineData("2026-10-19T12", "2026-10-19 12:00:00.000000", null)]
    [InlineData("2026-10-19 12:30", "2026-10-19 12:30:00.000000", null)]
    [InlineData("2026-10-19T12:30:45", "2026-10-19 12:30:45.000000", null)]
    [InlineData("2026-10-19T12:30:45.5", "2026-10-19 12:30:45.500000", null)]
    [InlineData("2026-10-19T12:30:45,25", "2026-10-19 12:30:45.250000", null)]
    [InlineData("2026-10-19T12:30:45.1234567", "2026-10-19 12:30:45.123456", null)]
    [InlineData("2026-10-19T12:30:45Z", "2026-10-19 12:30:45.000000", 0)]
    [InlineData("2026-10-19T12Z", "2026-10-19 12:00:00.000000", 0)]
    [InlineData("2026-10-19T12:30:45+10", "2026-10-19 12:30:45.000000", 36000)]
    [InlineData("2026-10-19T12:30:45-0530", "2026-10-19 12:30:45.000000", -19800)]
    [InlineData("2026-10-19T12:30:45+05:30", "2026-10-19 12:30:45.000000", 19800)]
    [InlineData("2026-10-19T12:30:45+05:30:15", "2026-10-19 12:30:45.000000", 19815)]
    [InlineData("2026-10-19T12:30+00:00", "2026-10-19 12:30:00.000000", 0)]
    [InlineData("2024-02-29T23:59:59", "2024-02-29 23:59:59.000000", null)]
    public void Iso_text_in_each_form_weir_meets_is_read(string text, string sqlite, int? offsetSeconds)
    {
        Assert.True(Timestamp.TryFromIsoFormat(text, out var value));
        Assert.Equal(sqlite, value.ToSqlite());
        Assert.Equal(offsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, value.Offset);
    }

    [Fact]
    public void An_offset_with_microseconds_is_read_to_the_microsecond()
    {
        Assert.True(Timestamp.TryFromIsoFormat("2026-10-19T12:30:45-05:30:15.000123", out var value));

        Assert.Equal(-(new TimeSpan(5, 30, 15).Add(TimeSpan.FromTicks(1230))), value.Offset);
    }

    [Fact]
    public void What_is_written_is_read_back_unchanged()
    {
        var written = new Timestamp(Clock.AddTicks(1_230), new TimeSpan(-3, -30, 0));

        Assert.True(Timestamp.TryFromIsoFormat(written.IsoFormat(), out var read));
        Assert.Equal(written, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-1")]
    [InlineData("2026-1x-19")]
    [InlineData("20261x19")]
    [InlineData("2026/10/19")]
    [InlineData("2026-10-19T")]
    [InlineData("2026-10-19T1x")]
    [InlineData("2026-10-19T12:")]
    [InlineData("2026-10-19T12:3x")]
    [InlineData("2026-10-19T12:30:")]
    [InlineData("2026-10-19T12:30:4x")]
    [InlineData("2026-10-19T12:30:45.")]
    [InlineData("2026-10-19T12:30:45.Z")]
    [InlineData("20261019T123045")]
    [InlineData("2026-10-19T12:30:45Q")]
    [InlineData("2026-10-19T12:30:45Zx")]
    [InlineData("2026-10-19T12:30:45-")]
    [InlineData("2026-10-19T12:30:45+5")]
    [InlineData("2026-10-19T12:30:45+05:3")]
    [InlineData("2026-10-19T12:30:45+05:30:1")]
    [InlineData("2026-10-19T12:30:45+05:30:15x123456")]
    [InlineData("2026-10-19T12:30:45+05:30:15.12")]
    [InlineData("2026-10-19T12:30:45+05:30:15.12345x")]
    [InlineData("2026-10-19T12:30:45+0x:30")]
    [InlineData("2026-10-19T12:30:45+24:00")]
    [InlineData("2026-10-19T12:30:45+05:60")]
    [InlineData("2026-10-19T12:30:45+05:30:60")]
    [InlineData("0000-01-01")]
    [InlineData("2026-00-10")]
    [InlineData("2026-13-10")]
    [InlineData("2026-10-00")]
    [InlineData("2026-11-31")]
    [InlineData("2025-02-29")]
    [InlineData("2026-10-19T24:00:00")]
    [InlineData("2026-10-19T12:60:00")]
    [InlineData("2026-10-19T12:30:60")]
    public void Text_that_is_not_a_date_time_is_not_read_and_leaves_the_default(string? text)
    {
        Assert.False(Timestamp.TryFromIsoFormat(text!, out var value));
        Assert.Equal(default, value);
    }
}
