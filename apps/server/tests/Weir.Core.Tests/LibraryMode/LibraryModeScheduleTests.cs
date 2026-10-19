using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Time;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>Library mode's scheduled scan and clean runs once a day, inside the library's own window.</summary>
public sealed class LibraryModeScheduleTests
{
    private const string EveryDay = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";

    // A Wednesday (2026-09-23), 10:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static LibraryAdmissionSnapshot NoWindow(bool enabled = true) =>
        new(1, enabled, ScheduleEnabled: true, ScheduleGrid: null, ScheduleHoursLimited: false, null, null, null, 0);

    private static LibraryAdmissionSnapshot Grid(string grid) =>
        new(1, true, ScheduleEnabled: true, grid, ScheduleHoursLimited: false, null, null, null, 0);

    private static LibraryAdmissionSnapshot Hours(string days, string start, string end) =>
        new(1, true, ScheduleEnabled: true, ScheduleGrid: null, ScheduleHoursLimited: true, days, start, end, 0);

    [Fact]
    public void A_library_never_scanned_on_schedule_is_due_now()
    {
        Assert.Equal(Now, LibraryModeSchedule.NextRunAt(null, NoWindow(), "UTC", Now));
    }

    [Fact]
    public void The_next_run_is_a_day_after_the_last()
    {
        var last = Now.AddHours(-3);
        Assert.Equal(last.AddDays(1), LibraryModeSchedule.NextRunAt(last, NoWindow(), "UTC", Now));
    }

    [Fact]
    public void An_overdue_run_keeps_its_own_time_rather_than_now()
    {
        // The timer ticks a few seconds after the run was due; the next one is still a day after the due time.
        var last = Now.AddDays(-1).AddSeconds(-20);
        Assert.Equal(last.AddDays(1), LibraryModeSchedule.NextRunAt(last, NoWindow(), "UTC", Now));
    }

    [Fact]
    public void A_run_due_while_the_window_is_shut_waits_for_it_to_open()
    {
        // Open 02:00 to 04:00 every day; the last run was at 02:00 today, so tomorrow at 02:00 is inside the window.
        var grid = ScheduleGrid.FromDaysAndTimes(EveryDay, "02:00", "04:00");
        var last = new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(last.AddDays(1), LibraryModeSchedule.NextRunAt(last, Grid(grid), "UTC", Now));

        // Never run before and it is 10:00 now: the window next opens at 02:00 tomorrow.
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 2, 0, 0, TimeSpan.Zero), LibraryModeSchedule.NextRunAt(null, Grid(grid), "UTC", Now));
    }

    [Fact]
    public void A_day_and_hours_window_opens_on_the_minute_it_names()
    {
        // Saturdays from 01:10. Wednesday 10:00 now, so the next opening is Saturday 26 September at 01:10.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 1, 10, 0, TimeSpan.Zero),
            LibraryModeSchedule.NextRunAt(null, Hours("Sat", "01:10", "05:00"), "UTC", Now));
    }

    [Fact]
    public void The_window_is_read_in_the_instance_time_zone()
    {
        // 02:00 to 04:00 in Sydney (UTC+10 in September) is 16:00 to 18:00 UTC the day before.
        var grid = ScheduleGrid.FromDaysAndTimes(EveryDay, "02:00", "04:00");
        Assert.Equal(
            new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.Zero),
            LibraryModeSchedule.NextRunAt(null, Grid(grid), "Australia/Sydney", Now));
    }

    [Fact]
    public void A_window_with_no_open_hour_or_a_switched_off_library_never_runs()
    {
        Assert.Null(LibraryModeSchedule.NextRunAt(null, Grid(new string('0', ScheduleGrid.SlotsPerWeek)), "UTC", Now));
        Assert.Null(LibraryModeSchedule.NextRunAt(null, NoWindow(enabled: false), "UTC", Now));
    }

    /// <summary>
    /// Runs the schedule for six days from the day before a clock change: each run is asked for when the one before it finished,
    /// as the timer does, so the answer after a clock change is what the following days are worked out from.
    /// </summary>
    private static List<DateTimeOffset> RunsAround(string zoneName, DateOnly dayBefore, string start, string end)
    {
        var zone = TimeZones.Find(zoneName);
        var window = Grid(ScheduleGrid.FromDaysAndTimes(EveryDay, start, end));
        var startWall = dayBefore.ToDateTime(TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        var last = TimeZones.FromWallClock(startWall, zone).AddSeconds(20);
        var runs = new List<DateTimeOffset> { last };
        for (var day = 0; day < 6; day++)
        {
            var next = LibraryModeSchedule.NextRunAt(last, window, zoneName, last);
            Assert.NotNull(next);
            last = next.Value.AddSeconds(20);
            runs.Add(next.Value);
        }

        return runs;
    }

    [Theory]
    [InlineData("Australia/Sydney", "2026-10-03", "02:15", "02:45")]
    [InlineData("Australia/Sydney", "2026-04-04", "02:15", "02:45")]
    [InlineData("Europe/London", "2026-03-28", "01:15", "01:45")]
    [InlineData("Europe/London", "2026-10-24", "01:15", "01:45")]
    public void A_window_under_an_hour_is_run_once_every_day_across_a_clock_change(string zoneName, string dayBefore, string start, string end)
    {
        var runs = RunsAround(zoneName, DateOnly.Parse(dayBefore, System.Globalization.CultureInfo.InvariantCulture), start, end);

        var localDays = runs.Select(run => DateOnly.FromDateTime(TimeZones.ToLocal(run, zoneName).DateTime)).ToList();
        Assert.Equal(runs.Count, localDays.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, runs.Count).Select(offset => localDays[0].AddDays(offset)), localDays);
    }

    [Fact]
    public void A_window_the_clocks_skip_over_runs_at_the_first_moment_after_the_skipped_hour()
    {
        // Sydney's clocks go from 02:00 to 03:00 on Sunday 4 October 2026, so 02:15 to 02:45 never happens that day.
        var runs = RunsAround("Australia/Sydney", new DateOnly(2026, 10, 3), "02:15", "02:45");

        var sunday = TimeZones.ToLocal(runs[1], "Australia/Sydney");
        Assert.Equal(new DateOnly(2026, 10, 4), DateOnly.FromDateTime(sunday.DateTime));
        Assert.True(sunday.TimeOfDay >= TimeSpan.FromHours(3));
        Assert.True(sunday.TimeOfDay < TimeSpan.FromHours(4));
    }

    [Fact]
    public void The_run_after_a_skipped_window_is_back_in_the_window_the_next_day()
    {
        var runs = RunsAround("Australia/Sydney", new DateOnly(2026, 10, 3), "02:15", "02:45");

        var monday = TimeZones.ToLocal(runs[2], "Australia/Sydney");
        Assert.Equal(new DateOnly(2026, 10, 5), DateOnly.FromDateTime(monday.DateTime));
        Assert.Equal(new TimeSpan(2, 15, 0), monday.TimeOfDay);
    }

    [Fact]
    public void A_window_in_an_hour_the_clocks_repeat_runs_once_that_day()
    {
        // London's clocks go from 02:00 back to 01:00 on Sunday 25 October 2026, so 01:15 to 01:45 happens twice.
        var runs = RunsAround("Europe/London", new DateOnly(2026, 10, 24), "01:15", "01:45");

        var sunday = runs.Where(run => DateOnly.FromDateTime(TimeZones.ToLocal(run, "Europe/London").DateTime) == new DateOnly(2026, 10, 25));
        Assert.Single(sunday);
    }
}
