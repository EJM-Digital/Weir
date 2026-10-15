using Weir.Core.Jobs;

namespace Weir.Core.Tests.Jobs;

/// <summary>The weekly schedule grid and its wall-clock rules.</summary>
public sealed class ScheduleGridTests
{
    /// <summary>A Wednesday at 14:00 UTC.</summary>
    internal static readonly DateTimeOffset Now = new(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_grid_means_no_restriction_and_an_all_zero_grid_means_never()
    {
        Assert.True(ScheduleGrid.Allows(string.Empty, "UTC", Now));
        Assert.True(ScheduleGrid.Allows(null, "UTC", Now));
        Assert.False(ScheduleGrid.Allows(new string('0', ScheduleGrid.SlotsPerWeek), "UTC", Now));
    }

    [Fact]
    public void A_grid_of_the_wrong_length_is_refused_rather_than_padded()
    {
        Assert.Contains("672 characters", Assert.Throws<ScheduleGridException>(() => ScheduleGrid.Normalize("101")).Message, StringComparison.Ordinal);
        Assert.Contains("only contain 0 and 1", Assert.Throws<ScheduleGridException>(() => ScheduleGrid.Normalize(new string('2', ScheduleGrid.SlotsPerWeek))).Message, StringComparison.Ordinal);
        Assert.Equal(
            "A schedule grid must be exactly 672 characters (7 days x 96 quarter-hours); this one has 3.",
            Assert.Throws<ScheduleGridException>(() => ScheduleGrid.Normalize("101")).Message);
    }

    [Fact]
    public void A_malformed_stored_grid_allows_work_rather_than_stopping_it()
    {
        Assert.True(ScheduleGrid.Allows("nonsense", "UTC", Now));
    }

    [Fact]
    public void The_grid_is_read_at_quarter_hour_resolution()
    {
        var slots = new string('0', ScheduleGrid.SlotsPerWeek).ToCharArray();
        slots[ScheduleGrid.SlotIndex(weekday: 2, hour: 14, minute: 15)] = '1';
        var grid = new string(slots);

        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now.AddMinutes(14)));
        Assert.True(ScheduleGrid.Allows(grid, "UTC", Now.AddMinutes(15)));
        Assert.True(ScheduleGrid.Allows(grid, "UTC", Now.AddMinutes(29)));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now.AddMinutes(30)));
    }

    [Fact]
    public void The_grid_is_evaluated_in_the_suite_timezone()
    {
        var slots = new string('0', ScheduleGrid.SlotsPerWeek).ToCharArray();
        // 14:00 UTC is 10:00 in New York on this date.
        slots[ScheduleGrid.SlotIndex(weekday: 2, hour: 10, minute: 0)] = '1';
        var grid = new string(slots);

        Assert.True(ScheduleGrid.Allows(grid, "America/New_York", Now));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now));
    }

    [Fact]
    public void An_unknown_timezone_falls_back_to_utc_rather_than_raising()
    {
        var slots = new string('0', ScheduleGrid.SlotsPerWeek).ToCharArray();
        slots[ScheduleGrid.SlotIndex(weekday: 2, hour: 14, minute: 0)] = '1';

        Assert.True(ScheduleGrid.Allows(new string(slots), "Mars/Olympus", Now));
    }

    [Fact]
    public void A_grid_built_from_days_and_times_reproduces_that_window()
    {
        var grid = ScheduleGrid.FromDaysAndTimes("mon,wed", "09:00", "17:00");

        Assert.True(ScheduleGrid.Allows(grid, "UTC", Now));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now.AddHours(4)));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now.AddDays(1)));
    }

    [Fact]
    public void An_overnight_window_carries_into_the_next_day()
    {
        var grid = ScheduleGrid.FromDaysAndTimes("wed", "22:00", "04:00");

        Assert.True(ScheduleGrid.Allows(grid, "UTC", Now.AddHours(9)));
        Assert.True(ScheduleGrid.Allows(grid, "UTC", Now.AddDays(1).AddHours(-12)));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", Now.AddHours(-2)));
    }

    [Fact]
    public void A_time_with_spaces_around_its_numbers_reads_like_the_plain_time()
    {
        var spaced = ScheduleGrid.FromDaysAndTimes("mon", " 09 : 30 ", "17:00");

        Assert.NotEqual(string.Empty, spaced);
        Assert.Equal(ScheduleGrid.FromDaysAndTimes("mon", "09:30", "17:00"), spaced);
    }

    [Fact]
    public void Days_that_cannot_be_parsed_become_no_restriction_not_a_wrong_window()
    {
        Assert.Equal(string.Empty, ScheduleGrid.FromDaysAndTimes(string.Empty, "09:00", "17:00"));
        Assert.Equal(string.Empty, ScheduleGrid.FromDaysAndTimes("mon", "nonsense", "17:00"));
    }

    [Theory]
    [InlineData("09:75", "17:00")]
    [InlineData("09:00", "17:-30")]
    [InlineData("24:00", "17:00")]
    [InlineData("1073741824:00", "17:00")]
    public void A_time_that_is_not_a_time_of_day_becomes_no_restriction_not_a_shifted_window(string start, string end) =>
        Assert.Equal(string.Empty, ScheduleGrid.FromDaysAndTimes("mon", start, end));

    [Fact]
    public void A_closed_window_reports_when_it_next_opens()
    {
        var slots = new string('0', ScheduleGrid.SlotsPerWeek).ToCharArray();
        slots[ScheduleGrid.SlotIndex(weekday: 2, hour: 16, minute: 0)] = '1';

        Assert.Equal(new DateTimeOffset(2026, 8, 26, 16, 0, 0, TimeSpan.Zero), ScheduleGrid.NextOpenSlot(new string(slots), "UTC", Now));
    }

    [Fact]
    public void A_grid_that_never_opens_reports_no_time_rather_than_a_wrong_one()
    {
        Assert.Null(ScheduleGrid.NextOpenSlot(new string('0', ScheduleGrid.SlotsPerWeek), "UTC", Now));
        Assert.Null(ScheduleGrid.NextOpenSlot(string.Empty, "UTC", Now));
    }

    [Fact]
    public void Next_open_slot_is_reported_in_utc_for_a_local_grid()
    {
        var slots = new string('0', ScheduleGrid.SlotsPerWeek).ToCharArray();
        slots[ScheduleGrid.SlotIndex(weekday: 2, hour: 12, minute: 30)] = '1';

        // 10:00 in New York now; 12:30 New York is 16:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 16, 30, 0, TimeSpan.Zero), ScheduleGrid.NextOpenSlot(new string(slots), "America/New_York", Now));
    }

    [Fact]
    public void Weekday_numbering_starts_at_zero_on_monday()
    {
        Assert.Equal(0, ScheduleGrid.MondayFirstDayIndex(DayOfWeek.Monday));
        Assert.Equal(6, ScheduleGrid.MondayFirstDayIndex(DayOfWeek.Sunday));
        Assert.Equal(2 * 96 + 14 * 4 + 3, ScheduleGrid.SlotIndex(2, 14, 45));
    }

    [Theory]
    [InlineData("", "09:00", "17:00", false)]
    [InlineData("Wed", "09:00", "17:00", true)]
    [InlineData("Mon,Tue", "09:00", "17:00", false)]
    [InlineData("wed", "09:00", "17:00", true)] // no valid tokens: every day
    [InlineData("Wed", "15:00", "17:00", false)]
    [InlineData("Wed", "22:00", "15:00", true)] // overnight, before the end
    [InlineData("Wed", "bad", "14:00", true)] // unparseable start falls back to 00:00
    [InlineData("Wed", "09:00", "14:00", true)] // end is inclusive
    public void Wall_clock_windows_match_schedule_time_window_active(string days, string start, string end, bool expected)
    {
        Assert.Equal(expected, ScheduleWallClock.TimeWindowActive(true, days, start, end, "UTC", Now));
    }

    [Fact]
    public void A_disabled_wall_clock_schedule_always_allows_work()
    {
        Assert.True(ScheduleWallClock.TimeWindowActive(false, string.Empty, "00:00", "00:00", "UTC", Now));
    }
}
