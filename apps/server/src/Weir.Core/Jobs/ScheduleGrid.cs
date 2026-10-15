using Weir.Core.Configuration;
using Weir.Core.Time;

namespace Weir.Core.Jobs;

/// <summary>The stored schedule grid is not a usable schedule.</summary>
public sealed class ScheduleGridException : Exception
{
    public ScheduleGridException()
    {
    }

    public ScheduleGridException(string message)
        : base(message)
    {
    }

    public ScheduleGridException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A 7x24 schedule at 15-minute resolution stored as 672 <c>0</c>/<c>1</c> characters.
/// Day 0 is Monday. An empty grid means no restriction.
/// </summary>
public static class ScheduleGrid
{
    public const int SlotsPerHour = 4;
    public const int SlotsPerDay = 24 * SlotsPerHour;
    public const int SlotsPerWeek = 7 * SlotsPerDay;
    public const int SlotMinutes = 60 / SlotsPerHour;

    /// <summary>Monday-first day names, matching the grid's day order.</summary>
    public static readonly IReadOnlyList<string> DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Validates and canonicalises stored grid text.</summary>
    public static string Normalize(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.Length != SlotsPerWeek)
        {
            throw new ScheduleGridException(
                $"A schedule grid must be exactly {SlotsPerWeek} characters " +
                $"(7 days x {SlotsPerDay} quarter-hours); this one has {text.Length}.");
        }

        if (text.Any(c => c is not ('0' or '1')))
        {
            throw new ScheduleGridException("A schedule grid may only contain 0 and 1.");
        }

        return text;
    }

    /// <summary>The grid position of a weekday, hour and minute; Monday is weekday 0.</summary>
    public static int SlotIndex(int weekday, int hour, int minute) =>
        (FloorMod(weekday, 7) * SlotsPerDay) + (hour * SlotsPerHour) + FloorDiv(minute, SlotMinutes);

    /// <summary>
    /// An empty grid allows everything, and so does one that fails to parse, so a display bug never
    /// becomes a stoppage.
    /// </summary>
    public static bool Allows(string? grid, string? timezoneName, DateTimeOffset now)
    {
        string text;
        try
        {
            text = Normalize(grid);
        }
        catch (ScheduleGridException)
        {
            return true;
        }

        if (text.Length == 0)
        {
            return true;
        }

        var local = TimeZones.ToLocal(now, timezoneName);
        return text[SlotIndex(MondayFirstDayIndex(local.DayOfWeek), local.Hour, local.Minute)] == '1';
    }

    /// <summary>The grid a day/start/end window describes, or no restriction.</summary>
    public static string FromDaysAndTimes(string? days, string? start, string? end)
    {
        var wanted = (days ?? string.Empty).Split(',')
            .Where(d => d.Trim().Length > 0)
            .Select(d => Prefix3(d.Trim().ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return string.Empty;
        }

        if (!TryParseHourMinute(string.IsNullOrEmpty(start) ? "00:00" : start, out var startHour, out var startMinute) ||
            !TryParseHourMinute(string.IsNullOrEmpty(end) ? "23:59" : end, out var endHour, out var endMinute))
        {
            return string.Empty;
        }

        if (!IsTimeOfDay(startHour, startMinute) || !IsTimeOfDay(endHour, endMinute))
        {
            return string.Empty;
        }

        var startSlot = (startHour * SlotsPerHour) + FloorDiv(startMinute, SlotMinutes);
        var endSlot = (endHour * SlotsPerHour) + FloorDiv(endMinute, SlotMinutes);

        var slots = Enumerable.Repeat('0', SlotsPerWeek).ToArray();
        for (var dayIndex = 0; dayIndex < DayNames.Count; dayIndex++)
        {
            if (!wanted.Contains(Prefix3(DayNames[dayIndex].ToLowerInvariant())))
            {
                continue;
            }

            var baseSlot = dayIndex * SlotsPerDay;
            if (startSlot <= endSlot)
            {
                for (var s = startSlot; s <= endSlot; s++)
                {
                    slots[baseSlot + s] = '1';
                }
            }
            else
            {
                for (var s = startSlot; s < SlotsPerDay; s++)
                {
                    slots[baseSlot + s] = '1';
                }

                for (var s = 0; s <= endSlot; s++)
                {
                    slots[(((dayIndex + 1) % 7) * SlotsPerDay) + s] = '1';
                }
            }
        }

        return new string(slots);
    }

    /// <summary>When the grid next allows work, or null if never or unrestricted.</summary>
    public static DateTimeOffset? NextOpenSlot(string? grid, string? timezoneName, DateTimeOffset now)
    {
        string text;
        try
        {
            text = Normalize(grid);
        }
        catch (ScheduleGridException)
        {
            return null;
        }

        if (text.Length == 0 || !text.Contains('1', StringComparison.Ordinal))
        {
            return null;
        }

        var zone = TimeZones.Find(timezoneName);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = SlotIndex(MondayFirstDayIndex(local.DayOfWeek), local.Hour, local.Minute);
        for (var ahead = 1; ahead <= SlotsPerWeek; ahead++)
        {
            if (text[(start + ahead) % SlotsPerWeek] != '1')
            {
                continue;
            }

            // Minutes are added to the local *wall clock* (ignoring any DST transition in between), then
            // converted back to UTC with the offset in force at the result, so the answer is the slot the grid shows.
            var aligned = new DateTime(local.Year, local.Month, local.Day, local.Hour, (local.Minute / SlotMinutes) * SlotMinutes, 0, DateTimeKind.Unspecified);
            var wall = aligned.AddMinutes(SlotMinutes * ahead);
            return TimeZones.FromWallClock(wall, zone).ToUniversalTime();
        }

        return null;
    }

    /// <summary>The Monday-first day index the grid uses: Monday is 0, Sunday is 6.</summary>
    public static int MondayFirstDayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static bool IsTimeOfDay(int hour, int minute) => hour is >= 0 and <= 23 && minute is >= 0 and <= 59;

    private static string Prefix3(string value) => value.Length <= 3 ? value : value[..3];

    private static bool TryParseHourMinute(string text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        // "HH:MM": split on the first colon only, and both sides must parse as integers.
        var parts = text.Split(':', 2);
        return parts.Length == 2 &&
               TryParseInt(parts[0], out hour) &&
               TryParseInt(parts[1], out minute);
    }

    /// <summary>
    /// An integer with the configuration rules (optional sign, ASCII digits, single underscores between digits),
    /// ignoring surrounding whitespace so a time typed as <c>09 : 30</c> still reads; fails outside the int range.
    /// </summary>
    internal static bool TryParseInt(string raw, out int value)
    {
        value = 0;
        if (!ValueParsing.TryParseInt(raw.Trim(), out var parsed) || parsed is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }

    // Floor modulo: the result takes the divisor's sign, so -1 mod 7 is 6.
    private static int FloorMod(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    // Floor division: rounds toward negative infinity, so -1 / 15 is -1.
    private static int FloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);
}
