namespace Weir.E2E.Tests.Support;

/// <summary>The weeks a workflow's hours are drawn as: 7 days of 96 quarter-hours, Monday first, written as 0 and 1.</summary>
public static class WeekGrids
{
    public const int SlotsPerHour = 4;
    public const int SlotsPerWeek = 7 * 24 * SlotsPerHour;

    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Every hour on except the one given.</summary>
    public static string AllExcept(int day, int hour) => Build(slot => slot / SlotsPerHour != HourIndex(day, hour));

    /// <summary>Every hour off except the one given.</summary>
    public static string OnlyAt(int day, int hour) => Build(slot => slot / SlotsPerHour == HourIndex(day, hour));

    public static string None() => new('0', SlotsPerWeek);

    /// <summary>How many hours of the week are on.</summary>
    public static int HoursOn(string grid) => Enumerable.Range(0, grid.Length / SlotsPerHour).Count(hour => grid.Substring(hour * SlotsPerHour, SlotsPerHour).Contains('1', StringComparison.Ordinal));

    /// <summary>The day (Monday 0) and hour of a moment, read in UTC.</summary>
    public static (int Day, int Hour) Of(DateTime utc) => (((int)utc.DayOfWeek + 6) % 7, utc.Hour);

    /// <summary>How the screen writes the start of an hour: "Wed 03:00".</summary>
    public static string Words(int day, int hour) => $"{DayNames[day]} {hour:00}:00";

    private static int HourIndex(int day, int hour) => (day * 24) + hour;

    private static string Build(Func<int, bool> on) => new([.. Enumerable.Range(0, SlotsPerWeek).Select(slot => on(slot) ? '1' : '0')]);
}
