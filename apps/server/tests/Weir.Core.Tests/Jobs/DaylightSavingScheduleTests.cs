using Weir.Core.Jobs;
using Weir.Core.LibraryMode;

namespace Weir.Core.Tests.Jobs;

/// <summary>
/// A weekly window is drawn on the wall clock of the suite's time zone, so on the two days a year the clocks change it must still open
/// when the clock shows its hours: the hour that is skipped has no window, and the hour that happens twice has it twice.
/// New York in 2026: clocks went forward at 02:00 on Sunday 8 March (to 03:00) and back at 02:00 on Sunday 1 November (to 01:00).
/// </summary>
public sealed class DaylightSavingScheduleTests
{
    private const string Zone = "America/New_York";
    private const string Sunday = "Sun";

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string SundayWindow(string start, string end) => ScheduleGrid.FromDaysAndTimes(Sunday, start, end);

    [Fact]
    public void A_window_after_the_clocks_go_forward_opens_when_the_new_clock_shows_it()
    {
        var grid = SundayWindow("03:00", "03:59");

        Assert.False(ScheduleGrid.Allows(grid, Zone, Utc(3, 8, 6, 59)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(3, 8, 7)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(3, 8, 7, 59)));
        Assert.False(ScheduleGrid.Allows(grid, Zone, Utc(3, 8, 8)));
    }

    [Fact]
    public void A_window_in_the_hour_the_clocks_skip_does_not_open_that_day_and_opens_the_next_Sunday()
    {
        var grid = SundayWindow("02:00", "02:59");

        Assert.All([Utc(3, 8, 6), Utc(3, 8, 6, 30), Utc(3, 8, 6, 59), Utc(3, 8, 7), Utc(3, 8, 7, 30)], at => Assert.False(ScheduleGrid.Allows(grid, Zone, at)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(3, 15, 6)));
    }

    [Fact]
    public void A_window_in_the_hour_the_clocks_repeat_is_open_both_times()
    {
        var grid = SundayWindow("01:00", "01:59");

        Assert.False(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 4, 59)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 5)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 5, 59)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 6)));
        Assert.True(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 6, 59)));
        Assert.False(ScheduleGrid.Allows(grid, Zone, Utc(11, 1, 7)));
    }

    [Fact]
    public void The_next_opening_is_the_slot_the_grid_shows_across_the_change_forward()
    {
        var grid = SundayWindow("03:00", "03:59");

        Assert.Equal(Utc(3, 8, 7), ScheduleGrid.NextOpenSlot(grid, Zone, Utc(3, 7, 12)));
        Assert.Equal(Utc(3, 15, 7), ScheduleGrid.NextOpenSlot(grid, Zone, Utc(3, 8, 12)));
    }

    [Fact]
    public void The_next_opening_is_the_slot_the_grid_shows_across_the_change_back()
    {
        var grid = SundayWindow("03:00", "03:59");

        Assert.Equal(Utc(11, 1, 8), ScheduleGrid.NextOpenSlot(grid, Zone, Utc(10, 31, 12)));
        Assert.Equal(Utc(11, 8, 8), ScheduleGrid.NextOpenSlot(grid, Zone, Utc(11, 1, 12)));
    }

    [Fact]
    public void A_daily_run_due_inside_a_wide_window_keeps_its_time_across_the_change_forward()
    {
        var window = Window(ScheduleGrid.FromDaysAndTimes("Mon,Tue,Wed,Thu,Fri,Sat,Sun", "02:00", "04:00"));
        var last = Utc(3, 7, 7, 30);

        Assert.Equal(Utc(3, 8, 7, 30), LibraryModeSchedule.NextRunAt(last, window, Zone, Utc(3, 8, 0)));
    }

    [Fact]
    public void A_daily_run_due_before_its_window_across_the_change_back_waits_for_the_window_on_the_same_day()
    {
        var window = Window(ScheduleGrid.FromDaysAndTimes("Mon,Tue,Wed,Thu,Fri,Sat,Sun", "02:00", "04:00"));
        var last = Utc(10, 31, 6, 30);

        // A day after 02:30 on Saturday is 01:30 on Sunday, an hour before the window; the window opens at 02:00 by the clock then.
        Assert.Equal(Utc(11, 1, 7), LibraryModeSchedule.NextRunAt(last, window, Zone, Utc(11, 1, 0)));
    }

    private static LibraryAdmissionSnapshot Window(string grid) =>
        new(1, true, ScheduleEnabled: true, grid, ScheduleHoursLimited: false, null, null, null, 0);
}
