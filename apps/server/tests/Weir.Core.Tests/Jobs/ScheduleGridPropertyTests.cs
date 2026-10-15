using FsCheck;
using FsCheck.Fluent;
using Weir.Core.Jobs;
using Weir.TestSupport;

namespace Weir.Core.Tests.Jobs;

/// <summary>
/// <see cref="ScheduleGrid"/> over every grid, window and instant: stored text is a grid or a named refusal, a window drawn as a grid
/// allows exactly the quarter-hours it names, and the next open slot is the first one the grid allows.
/// </summary>
public sealed class ScheduleGridPropertyTests
{
    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private sealed record Window(IReadOnlyList<int> DayIndexes, int StartHour, int StartMinute, int EndHour, int EndMinute);

    private static readonly Gen<Window> AnyWindow =
        from days in Gen.SubListOf(Enumerable.Range(0, 7).ToArray())
        from startHour in Gen.Choose(0, 23)
        from startMinute in Gen.Choose(0, 59)
        from endHour in Gen.Choose(0, 23)
        from endMinute in Gen.Choose(0, 59)
        select new Window([.. days], startHour, startMinute, endHour, endMinute);

    private static readonly Gen<string> AnyGrid =
        from slots in Gen.ArrayOf(Gen.Frequency((3, Gen.Constant('0')), (1, Gen.Constant('1'))), ScheduleGrid.SlotsPerWeek)
        select new string(slots);

    private static readonly Gen<DateTimeOffset> AnyInstant =
        Gen.Choose(0, 60 * 24 * 7 * 100).Select(minutes => new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private static string Time(int hour, int minute) => $"{hour:00}:{minute:00}";

    private static string DaysText(IEnumerable<int> indexes, bool upper) =>
        string.Join(upper ? " , " : ",", indexes.Select(index => upper ? Days[index].ToUpperInvariant() + "day" : Days[index]));

    [Fact]
    public void Stored_text_is_a_grid_of_the_right_length_or_nothing_or_a_named_refusal()
    {
        var text = Gen.OneOf(
            AnyGrid,
            AnyGrid.Select(grid => " \t" + grid + "\n"),
            Gen.Elements(string.Empty, "  ", "0", new string('1', 671), new string('0', 673), new string('2', 672)),
            Gen.Choose(0, 700).Select(length => new string('1', length)));

        PropertyChecks.ForAll(text, raw =>
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                Assert.Equal(string.Empty, ScheduleGrid.Normalize(raw));
            }
            else if (trimmed.Length == ScheduleGrid.SlotsPerWeek && trimmed.All(character => character is '0' or '1'))
            {
                Assert.Equal(trimmed, ScheduleGrid.Normalize(raw));
            }
            else
            {
                Assert.Throws<ScheduleGridException>(() => ScheduleGrid.Normalize(raw));
            }
        });
    }

    [Fact]
    public void A_window_is_drawn_as_exactly_the_quarter_hours_it_names_and_a_window_that_runs_past_midnight_continues_into_the_next_day()
    {
        PropertyChecks.ForAll(AnyWindow.Zip(AnyInstant), found =>
        {
            var (window, instant) = found;
            var grid = ScheduleGrid.FromDaysAndTimes(
                DaysText(window.DayIndexes, upper: false), Time(window.StartHour, window.StartMinute), Time(window.EndHour, window.EndMinute));

            if (window.DayIndexes.Count == 0)
            {
                Assert.Equal(string.Empty, grid);
                return;
            }

            Assert.Equal(ScheduleGrid.SlotsPerWeek, grid.Length);
            var startSlot = (window.StartHour * 4) + (window.StartMinute / 15);
            var endSlot = (window.EndHour * 4) + (window.EndMinute / 15);
            var day = ScheduleGrid.MondayFirstDayIndex(instant.DayOfWeek);
            var slot = (instant.Hour * 4) + (instant.Minute / 15);
            var yesterday = (day + 6) % 7;
            var expected = startSlot <= endSlot
                ? window.DayIndexes.Contains(day) && slot >= startSlot && slot <= endSlot
                : (window.DayIndexes.Contains(day) && slot >= startSlot) || (window.DayIndexes.Contains(yesterday) && slot <= endSlot);

            Assert.Equal(expected, ScheduleGrid.Allows(grid, "UTC", instant));
        });
    }

    [Fact]
    public void A_time_that_is_not_a_time_of_day_draws_no_window_at_all()
    {
        var notATime = Gen.OneOf(
            Gen.Elements("24:00", "09:60", "09:75", "00:-30", "-1:00", "1073741824:00", "1:2:3", ":", "ab:cd", string.Empty + "25:61", "09:99", "99:00"),
            from hour in Gen.OneOf(Gen.Choose(24, 99), Gen.Choose(-5, -1))
            from minute in Gen.Choose(0, 59)
            select Time(hour, minute));

        PropertyChecks.ForAll(notATime.Zip(Gen.Elements(true, false)), found =>
        {
            var (bad, atStart) = found;
            var grid = atStart
                ? ScheduleGrid.FromDaysAndTimes("mon,tue", bad, "10:00")
                : ScheduleGrid.FromDaysAndTimes("mon,tue", "08:00", bad);
            Assert.Equal(string.Empty, grid);
        });
    }

    [Fact]
    public void The_next_open_slot_is_the_first_quarter_hour_after_now_the_grid_allows()
    {
        PropertyChecks.ForAll(AnyGrid.Zip(AnyInstant), found =>
        {
            var (grid, now) = found;
            var next = ScheduleGrid.NextOpenSlot(grid, "UTC", now);

            if (!grid.Contains('1', StringComparison.Ordinal))
            {
                Assert.Null(next);
                return;
            }

            Assert.NotNull(next);
            Assert.True(next > now);
            Assert.True(ScheduleGrid.Allows(grid, "UTC", next.Value));
            Assert.Equal(0, next.Value.Minute % ScheduleGrid.SlotMinutes);
            var aligned = now.AddTicks(-(now.Ticks % TimeSpan.FromMinutes(ScheduleGrid.SlotMinutes).Ticks));
            for (var slot = aligned.AddMinutes(ScheduleGrid.SlotMinutes); slot < next; slot = slot.AddMinutes(ScheduleGrid.SlotMinutes))
            {
                Assert.False(ScheduleGrid.Allows(grid, "UTC", slot), $"{slot:u} is open and earlier than {next:u}");
            }
        });
    }

    [Fact]
    public void An_empty_grid_or_a_grid_that_is_not_one_allows_everything_and_has_no_next_slot()
    {
        var notAGrid = Gen.OneOf(Gen.Elements(string.Empty, " ", "1", "x"), Gen.Choose(1, 671).Select(length => new string('1', length)));
        PropertyChecks.ForAll(notAGrid.Zip(AnyInstant), found =>
        {
            Assert.True(ScheduleGrid.Allows(found.Item1, "UTC", found.Item2));
            Assert.Null(ScheduleGrid.NextOpenSlot(found.Item1, "UTC", found.Item2));
        });
    }

    [Fact]
    public void A_slot_index_is_inside_the_week_for_any_weekday_hour_and_minute_of_a_day()
    {
        PropertyChecks.ForAll(Gen.Choose(-20, 20).Zip(Gen.Choose(0, 23)), found =>
        {
            foreach (var minute in new[] { 0, 14, 15, 59 })
            {
                Assert.InRange(ScheduleGrid.SlotIndex(found.Item1, found.Item2, minute), 0, ScheduleGrid.SlotsPerWeek - 1);
            }
        });
    }
}
