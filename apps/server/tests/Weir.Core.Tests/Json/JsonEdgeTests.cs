using System.Numerics;
using Weir.Core.Json;
using Weir.Core.Time;

namespace Weir.Core.Tests.Json;

/// <summary>JSON text that stops short or is written out of the ordinary, the values the database cannot hold, and the names of the value kinds.</summary>
public sealed class JsonEdgeTests
{
    [Theory]
    [InlineData("-")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("Inf")]
    [InlineData("-Inf")]
    [InlineData("{")]
    [InlineData("{\"a\"")]
    [InlineData("{ ")]
    [InlineData("[1")]
    [InlineData("[")]
    [InlineData("\"abc\\")]
    public void Text_that_stops_short_is_refused(string text)
    {
        Assert.Throws<WireJsonDecodeException>(() => WireJsonParser.Parse(text));
    }

    [Theory]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    public void The_infinities_earlier_releases_wrote_still_read(string text, double expected)
    {
        Assert.Equal(expected, ((WireNumber)WireJsonParser.Parse(text)).Value);
    }

    [Fact]
    public void An_escaped_slash_is_a_slash()
    {
        Assert.Equal("a/b", ((WireString)WireJsonParser.Parse("\"a\\/b\"")).Value);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x41 }, "A")]
    [InlineData(new byte[] { 0x41, 0x00 }, "A")]
    [InlineData(new byte[] { 0x7B, 0x7D }, "{}")]
    public void Two_bytes_are_read_as_utf_16_when_one_of_them_is_zero_and_as_utf_8_otherwise(byte[] bytes, string expected)
    {
        Assert.Equal(expected, WireJsonParser.DecodeBytes(bytes));
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    public void A_decimal_number_that_is_not_an_ordinary_one_is_written_the_way_earlier_releases_wrote_it(double value, string expected)
    {
        Assert.Equal(expected, WireJsonWriter.FloatRepr(value));
    }

    [Fact]
    public void A_date_and_time_has_to_be_turned_into_text_before_it_is_written()
    {
        var value = new WireTimestampValue(Timestamp.FromUtc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Throws<InvalidOperationException>(() => WireJsonWriter.Dumps(value, WireJsonFormat.Compact));
    }

    [Fact]
    public void The_kinds_of_value_are_named_the_way_the_wording_clients_see_expects()
    {
        Assert.Equal("bool", WireBool.True.WireTypeName);
        Assert.Equal("int", new WireInteger(BigInteger.One).WireTypeName);
        Assert.Equal("float", new WireNumber(1.5).WireTypeName);
        Assert.Equal("str", new WireString("x").WireTypeName);
        Assert.Equal("datetime", new WireTimestampValue(Timestamp.FromUtc(DateTime.UnixEpoch)).WireTypeName);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.5, true)]
    public void A_decimal_number_is_set_unless_it_is_zero(double value, bool expected)
    {
        Assert.Equal(expected, new WireNumber(value).IsTruthy);
    }

    [Fact]
    public void An_object_is_changed_through_its_indexer_and_forgets_a_key_it_never_had()
    {
        var value = new WireObject();

        value["a"] = new WireString("x");

        Assert.Equal("x", ((WireString)value["a"]).Value);
        Assert.False(value.Remove("missing"));
        Assert.True(value.Remove("a"));
    }

    [Fact]
    public void A_database_value_of_a_kind_the_database_does_not_return_is_shown_as_text()
    {
        Assert.Equal("1.5", ((WireString)WireConvert.FromDatabase(1.5m)).Value);
    }

    [Fact]
    public void Only_a_whole_number_can_be_read_as_one_and_the_refusal_names_what_was_found()
    {
        var nothing = Assert.Throws<WireTypeException>(() => WireConvert.ToInt(WireNull.Instance));
        var list = Assert.Throws<WireTypeException>(() => WireConvert.ToInt(new WireArray()));
        var item = Assert.Throws<WireTypeException>(() => WireConvert.ToInt(new WireObject()));

        Assert.Equal("Expected a whole number but found nothing.", nothing.Message);
        Assert.Equal("Expected a whole number but found a list.", list.Message);
        Assert.Equal("Expected a whole number but found an object.", item.Message);
    }

    [Fact]
    public void A_list_or_object_cannot_be_stored_in_one_database_column()
    {
        var list = Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(new WireArray()));
        var item = Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(new WireObject()));
        var when = Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(new WireTimestampValue(Timestamp.FromUtc(DateTime.UnixEpoch))));

        Assert.Contains("a list", list.Message, StringComparison.Ordinal);
        Assert.Contains("an object", item.Message, StringComparison.Ordinal);
        Assert.Contains("a date and time", when.Message, StringComparison.Ordinal);
    }
}
