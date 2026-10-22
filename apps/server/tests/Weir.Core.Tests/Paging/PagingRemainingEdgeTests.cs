using Weir.Core.Paging;

namespace Weir.Core.Tests.Paging;

/// <summary>The last edges of paged lists: keys with a missing part, text that is not letters, a cursor holding something it cannot, and direction names.</summary>
public sealed class PagingRemainingEdgeTests
{
    private static readonly KeysetPart[] Ignoring = [new(KeysetValueKind.TextIgnoringCase, SortDirection.Ascending)];

    [Fact]
    public void A_key_with_a_missing_part_comes_before_one_that_has_it()
    {
        KeysetPart[] parts = [new(KeysetValueKind.Number, SortDirection.Ascending)];

        Assert.True(KeysetOrder.Compare([null], [1L], parts) < 0);
        Assert.True(KeysetOrder.Compare([1L], [null], parts) > 0);
        Assert.Equal(0, KeysetOrder.Compare([null], [null], parts));
    }

    [Theory]
    [InlineData("1a", "Aa", -1)]
    [InlineData("a", "Z", -1)]
    [InlineData("Z", "a", 1)]
    [InlineData("a_", "A~", -1)]
    [InlineData("é", "Z", 1)]
    public void Text_ignoring_case_folds_only_the_ascii_capitals(string first, string second, int expected)
    {
        Assert.Equal(expected, Math.Sign(KeysetOrder.Compare([first], [second], Ignoring)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2.5)]
    public void A_cursor_holds_only_whole_numbers_and_text(object value)
    {
        Assert.Throws<ArgumentException>(() => KeysetCursor.Encode("name", SortDirection.Ascending, [value]));
    }

    [Theory]
    [InlineData("asc", true, SortDirection.Ascending)]
    [InlineData("desc", true, SortDirection.Descending)]
    [InlineData("sideways", false, SortDirection.Descending)]
    [InlineData(null, false, SortDirection.Descending)]
    public void A_direction_name_is_ascending_descending_or_neither(string? name, bool known, SortDirection direction)
    {
        Assert.Equal(known, SortDirections.TryParse(name, out var parsed));
        Assert.Equal(direction, parsed);
    }
}
