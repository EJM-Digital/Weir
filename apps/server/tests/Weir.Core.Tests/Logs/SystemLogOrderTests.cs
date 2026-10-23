using Weir.Core.Json;
using Weir.Core.Logs;
using Weir.Core.Paging;

namespace Weir.Core.Tests.Logs;

/// <summary>The orders System › Logs can be listed in, and the cursor a page hands to the next.</summary>
public sealed class SystemLogOrderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<SystemLogSort, SortDirection> EveryOrder()
    {
        var orders = new TheoryData<SystemLogSort, SortDirection>();
        foreach (var sort in Enum.GetValues<SystemLogSort>())
        {
            orders.Add(sort, SortDirection.Ascending);
            orders.Add(sort, SortDirection.Descending);
        }

        return orders;
    }

    [Theory]
    [MemberData(nameof(EveryOrder))]
    public void A_cursor_comes_back_as_the_key_of_the_row_it_was_made_from(SystemLogSort sort, SortDirection direction)
    {
        var order = new SystemLogOrder(sort, direction);
        var key = order.KeyOf(Row(SystemLogSource.Job, 4121, 5, workflowId: 3), new Dictionary<long, string> { [3] = "Movies" });

        var decoded = order.TryDecodeCursor(order.EncodeCursor(key), out var back);

        Assert.True(decoded);
        Assert.Equal(key, back);
    }

    [Fact]
    public void A_cursor_is_safe_to_put_in_an_address()
    {
        var order = new SystemLogOrder(SystemLogSort.Workflow, SortDirection.Ascending);
        var key = order.KeyOf(Row(SystemLogSource.Server, 99, 5, workflowId: 3), new Dictionary<long, string> { [3] = "Films & série?" });

        Assert.Matches("^[A-Za-z0-9_-]+$", order.EncodeCursor(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("MTIzLjQuNQ")]
    public void Text_this_log_did_not_write_is_not_a_cursor(string? text)
    {
        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(text, out _));
    }

    [Fact]
    public void A_cursor_with_a_time_or_source_no_row_can_have_is_not_a_cursor()
    {
        var beyondTheCalendar = KeysetCursor.Encode(SystemLogSorts.Time, SortDirection.Descending, [long.MaxValue, 0L, 1L]);
        var unknownSource = KeysetCursor.Encode(SystemLogSorts.Time, SortDirection.Descending, [Noon.UtcTicks, 99L, 1L]);
        var oversizedSource = KeysetCursor.Encode(SystemLogSorts.Time, SortDirection.Descending, [Noon.UtcTicks, 1L << 32, 1L]);
        var beforeTheCalendar = KeysetCursor.Encode(SystemLogSorts.Time, SortDirection.Descending, [-1L, 0L, 1L]);

        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(beyondTheCalendar, out _));
        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(unknownSource, out _));
        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(oversizedSource, out _));
        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(beforeTheCalendar, out _));
    }

    [Fact]
    public void A_cursor_made_under_one_sort_or_direction_is_not_a_cursor_under_another()
    {
        var byLevel = new SystemLogOrder(SystemLogSort.Level, SortDirection.Ascending);
        var cursor = byLevel.EncodeCursor(byLevel.KeyOf(Row(SystemLogSource.Event, 1, 5), SystemLogOrder.NoWorkflowNames));

        Assert.True(byLevel.TryDecodeCursor(cursor, out _));
        Assert.False(new SystemLogOrder(SystemLogSort.Level, SortDirection.Descending).TryDecodeCursor(cursor, out _));
        Assert.False(new SystemLogOrder(SystemLogSort.Category, SortDirection.Ascending).TryDecodeCursor(cursor, out _));
        Assert.False(SystemLogOrder.Newest.TryDecodeCursor(cursor, out _));
    }

    [Fact]
    public void Newest_first_orders_by_later_time_then_events_before_jobs_before_the_server_then_higher_number()
    {
        var newest = Row(SystemLogSource.Server, 1, 4);
        var sameTimeEvent = Row(SystemLogSource.Event, 1, 5);
        var sameTimeJobHigh = Row(SystemLogSource.Job, 9, 5);
        var sameTimeJobLow = Row(SystemLogSource.Job, 2, 5);

        var ordered = Ordered(SystemLogOrder.Newest, sameTimeJobLow, newest, sameTimeEvent, sameTimeJobHigh);

        Assert.Equal([newest.Id, sameTimeEvent.Id, sameTimeJobHigh.Id, sameTimeJobLow.Id], ordered);
    }

    [Fact]
    public void Oldest_first_is_the_newest_first_order_read_backwards()
    {
        var rows = new[] { Row(SystemLogSource.Server, 1, 4), Row(SystemLogSource.Event, 1, 5), Row(SystemLogSource.Job, 9, 5), Row(SystemLogSource.Job, 2, 5) };

        var newestFirst = Ordered(SystemLogOrder.Newest, rows);
        var oldestFirst = Ordered(new SystemLogOrder(SystemLogSort.Time, SortDirection.Ascending), rows);

        Assert.Equal(newestFirst.AsEnumerable().Reverse(), oldestFirst);
    }

    [Fact]
    public void Levels_run_from_error_to_success_and_ties_fall_by_time_in_the_direction_of_the_sort()
    {
        var oldError = Row(SystemLogSource.Event, 1, 30, SystemLogLevels.Error);
        var newError = Row(SystemLogSource.Event, 2, 10, SystemLogLevels.Error);
        var warning = Row(SystemLogSource.Event, 3, 20, SystemLogLevels.Warning);
        var success = Row(SystemLogSource.Event, 4, 20, SystemLogLevels.Success);

        var ascending = Ordered(new SystemLogOrder(SystemLogSort.Level, SortDirection.Ascending), oldError, success, newError, warning);
        var descending = Ordered(new SystemLogOrder(SystemLogSort.Level, SortDirection.Descending), oldError, success, newError, warning);

        Assert.Equal([oldError.Id, newError.Id, warning.Id, success.Id], ascending);
        Assert.Equal([success.Id, warning.Id, newError.Id, oldError.Id], descending);
    }

    [Fact]
    public void A_missing_workflow_comes_last_whichever_way_the_sort_runs()
    {
        var names = new Dictionary<long, string> { [1] = "bravo", [2] = "Alpha" };
        var bravo = Row(SystemLogSource.Event, 1, 5, workflowId: 1);
        var alpha = Row(SystemLogSource.Event, 2, 5, workflowId: 2);
        var none = Row(SystemLogSource.Event, 3, 5);
        var gone = Row(SystemLogSource.Event, 4, 5, workflowId: 99);

        var ascending = Ordered(new SystemLogOrder(SystemLogSort.Workflow, SortDirection.Ascending), names, none, bravo, gone, alpha);
        var descending = Ordered(new SystemLogOrder(SystemLogSort.Workflow, SortDirection.Descending), names, none, bravo, gone, alpha);

        Assert.Equal([alpha.Id, bravo.Id, none.Id, gone.Id], ascending);
        Assert.Equal([bravo.Id, alpha.Id, gone.Id, none.Id], descending);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("level")]
    [InlineData("source")]
    [InlineData("category")]
    [InlineData("workflow")]
    public void A_sort_name_on_the_wire_is_the_sort_it_stands_for(string name)
    {
        Assert.True(SystemLogSorts.TryParse(name, out var sort));
        Assert.Equal(name, SystemLogSorts.NameOf(sort));
    }

    [Theory]
    [InlineData("Time")]
    [InlineData("message")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_is_not_a_sort_is_not_a_sort(string? name)
    {
        Assert.False(SystemLogSorts.TryParse(name, out _));
    }

    private static List<string> Ordered(SystemLogOrder order, params SystemLogRow[] rows) => Ordered(order, SystemLogOrder.NoWorkflowNames, rows);

    private static List<string> Ordered(SystemLogOrder order, IReadOnlyDictionary<long, string> names, params SystemLogRow[] rows) =>
        [.. rows
            .Select(row => (row.Id, Key: order.KeyOf(row, names)))
            .OrderBy(pair => pair.Key, Comparer<IReadOnlyList<object?>>.Create(order.Compare))
            .Select(pair => pair.Id)];

    private static SystemLogRow Row(SystemLogSource source, long key, int minutesAgo, string level = SystemLogLevels.Info, long? workflowId = null) => new(
        source,
        key,
        Noon - TimeSpan.FromMinutes(minutesAgo),
        level,
        SystemLogCategories.Weir,
        workflowId,
        "Something happened",
        null,
        new WireObject());

    [Fact]
    public void A_cursor_the_log_gave_out_gives_its_key_back_and_any_other_text_is_an_error()
    {
        var order = new SystemLogOrder(SystemLogSort.Level, SortDirection.Descending);
        var key = order.KeyOf(Row(SystemLogSource.Event, 1, 5), SystemLogOrder.NoWorkflowNames);

        Assert.Equal(key, order.KeyOfGivenCursor(order.EncodeCursor(key)));
        Assert.Throws<InvalidOperationException>(() => order.KeyOfGivenCursor("not a cursor"));
    }
}
