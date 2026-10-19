using System.Numerics;
using System.Text;
using Weir.Core.Json;

namespace Weir.Core.Tests.Json;

/// <summary>The display, integer and database conversions of JSON values, whose exact output is kept in stored data and API messages.</summary>
public sealed class WireConvertTests
{
    [Fact]
    public void The_exception_types_carry_a_message_and_the_error_that_caused_them()
    {
        var cause = new InvalidOperationException("inner");

        Assert.Equal("bad", new WireValueException("bad").Message);
        Assert.Same(cause, new WireValueException("bad", cause).InnerException);
        Assert.NotNull(new WireValueException().Message);
        Assert.Equal("wrong", new WireTypeException("wrong").Message);
        Assert.Same(cause, new WireTypeException("wrong", cause).InnerException);
        Assert.NotNull(new WireTypeException().Message);
    }

    [Fact]
    public void A_string_stays_itself_and_everything_else_takes_its_display_form()
    {
        Assert.Equal("plain", WireConvert.Str(new WireString("plain")));
        Assert.Equal("None", WireConvert.Str(WireNull.Instance));
        Assert.Equal("True", WireConvert.Str(WireBool.True));
        Assert.Equal("12", WireConvert.Str(new WireInteger(12)));
    }

    [Fact]
    public void Display_forms_match_what_earlier_releases_stored()
    {
        var list = new WireArray([new WireInteger(1), new WireString("a"), WireNull.Instance]);
        var nested = new WireObject().Set("k", 2L).Set("list", list).Set("flag", false);

        Assert.Equal("None", WireConvert.Repr(WireNull.Instance));
        Assert.Equal("True", WireConvert.Repr(WireBool.True));
        Assert.Equal("False", WireConvert.Repr(WireBool.False));
        Assert.Equal("-7", WireConvert.Repr(new WireInteger(-7)));
        Assert.Equal("1.5", WireConvert.Repr(new WireNumber(1.5)));
        Assert.Equal("[1, 'a', None]", WireConvert.Repr(list));
        Assert.Equal("[]", WireConvert.Repr(new WireArray()));
        Assert.Equal("{'k': 2, 'list': [1, 'a', None], 'flag': False}", WireConvert.Repr(nested));
        Assert.Equal("{}", WireConvert.Repr(new WireObject()));
    }

    [Fact]
    public void A_string_in_a_display_form_is_quoted_the_way_stored_data_has_it()
    {
        Assert.Equal("'plain'", WireConvert.Repr(new WireString("plain")));
        Assert.Equal(WireStrings.Repr("it's"), WireConvert.Repr(new WireString("it's")));
    }

    [Fact]
    public void Display_refuses_nothing_and_a_date_that_is_not_a_json_value()
    {
        Assert.Throws<ArgumentNullException>(() => WireConvert.Repr(null!));
        Assert.Throws<InvalidOperationException>(() => WireConvert.Repr(new WireTimestampValue(default)));
    }

    [Theory]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NegativeInfinity, "-inf")]
    [InlineData(0.25, "0.25")]
    [InlineData(-3.0, "-3.0")]
    public void A_float_shows_its_shortest_digits_and_the_names_of_its_special_values(double value, string expected) =>
        Assert.Equal(expected, WireConvert.FloatRepr(value));

    [Theory]
    [InlineData("42", "42")]
    [InlineData("  -42  ", "-42")]
    [InlineData("+7", "7")]
    [InlineData("1_000_000", "1000000")]
    [InlineData("-1_0", "-10")]
    [InlineData("99999999999999999999", "99999999999999999999")]
    public void An_integer_literal_allows_a_sign_spaces_and_single_underscores_between_digits(string raw, string expected)
    {
        Assert.True(WireConvert.TryParseIntLiteral(raw, out var value));
        Assert.Equal(BigInteger.Parse(expected), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("1__0")]
    [InlineData("1.5")]
    [InlineData("12abc")]
    [InlineData("0x10")]
    public void Anything_else_is_not_an_integer_literal_and_leaves_zero(string raw)
    {
        Assert.False(WireConvert.TryParseIntLiteral(raw, out var value));
        Assert.Equal(BigInteger.Zero, value);
    }

    [Fact]
    public void Digits_of_other_scripts_count_as_the_digits_they_stand_for()
    {
        Assert.True(WireConvert.TryParseIntLiteral("٣٤", out var value));
        Assert.Equal(new BigInteger(34), value);
    }

    [Fact]
    public void An_integer_literal_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => WireConvert.TryParseIntLiteral(null!, out _));
    }

    [Fact]
    public void Values_turn_into_whole_numbers_the_way_clients_expect()
    {
        Assert.Equal(new BigInteger(5), WireConvert.ToInt(new WireInteger(5)));
        Assert.Equal(BigInteger.One, WireConvert.ToInt(WireBool.True));
        Assert.Equal(BigInteger.Zero, WireConvert.ToInt(WireBool.False));
        Assert.Equal(new BigInteger(2), WireConvert.ToInt(new WireNumber(2.9)));
        Assert.Equal(new BigInteger(-2), WireConvert.ToInt(new WireNumber(-2.9)));
        Assert.Equal(new BigInteger(31), WireConvert.ToInt(new WireString(" 3_1 ")));
        Assert.Throws<ArgumentNullException>(() => WireConvert.ToInt(null!));
    }

    [Fact]
    public void A_number_or_text_that_is_not_whole_is_a_bad_value_and_infinity_a_wrong_type()
    {
        Assert.Equal("NaN is not a whole number.", Assert.Throws<WireValueException>(() => WireConvert.ToInt(new WireNumber(double.NaN))).Message);
        Assert.Equal("Infinity is not a whole number.", Assert.Throws<WireTypeException>(() => WireConvert.ToInt(new WireNumber(double.PositiveInfinity))).Message);
        Assert.Equal("Infinity is not a whole number.", Assert.Throws<WireTypeException>(() => WireConvert.ToInt(new WireNumber(double.NegativeInfinity))).Message);
        Assert.Equal("'1.5' is not a whole number.", Assert.Throws<WireValueException>(() => WireConvert.ToInt(new WireString("1.5"))).Message);
    }

    [Theory]
    [MemberData(nameof(KindsThatAreNotNumbers))]
    public void A_value_of_any_other_kind_names_what_it_is_instead(WireValue value, string kind) =>
        Assert.Equal($"Expected a whole number but found {kind}.", Assert.Throws<WireTypeException>(() => WireConvert.ToInt(value)).Message);

    public static TheoryData<WireValue, string> KindsThatAreNotNumbers() => new()
    {
        { WireNull.Instance, "nothing" },
        { new WireArray(), "a list" },
        { new WireObject(), "an object" },
        { new WireTimestampValue(default), "a date and time" },
    };

    [Fact]
    public void Truthiness_follows_the_value_and_a_missing_value_is_refused()
    {
        Assert.True(WireConvert.Bool(new WireString("x")));
        Assert.False(WireConvert.Bool(new WireString(string.Empty)));
        Assert.False(WireConvert.Bool(WireNull.Instance));
        Assert.Throws<ArgumentNullException>(() => WireConvert.Bool(null!));
    }

    [Fact]
    public void Database_values_become_json_values()
    {
        Assert.Same(WireNull.Instance, WireConvert.FromDatabase(null));
        Assert.Same(WireNull.Instance, WireConvert.FromDatabase(DBNull.Value));
        Assert.Equal(new BigInteger(9), Assert.IsType<WireInteger>(WireConvert.FromDatabase(9L)).Value);
        Assert.Equal(new BigInteger(8), Assert.IsType<WireInteger>(WireConvert.FromDatabase(8)).Value);
        Assert.Equal(2.5, Assert.IsType<WireNumber>(WireConvert.FromDatabase(2.5)).Value);
        Assert.Equal("text", Assert.IsType<WireString>(WireConvert.FromDatabase("text")).Value);
        Assert.Equal("blob", Assert.IsType<WireString>(WireConvert.FromDatabase(Encoding.UTF8.GetBytes("blob"))).Value);
        Assert.Same(WireBool.True, WireConvert.FromDatabase(true));
        Assert.Equal("1.5", Assert.IsType<WireString>(WireConvert.FromDatabase(1.5m)).Value);
    }

    [Fact]
    public void Json_values_become_database_parameters()
    {
        Assert.Same(DBNull.Value, WireConvert.ToDatabase(WireNull.Instance));
        Assert.Equal(1L, WireConvert.ToDatabase(WireBool.True));
        Assert.Equal(0L, WireConvert.ToDatabase(WireBool.False));
        Assert.Equal(long.MaxValue, WireConvert.ToDatabase(new WireInteger(long.MaxValue)));
        Assert.Equal(long.MinValue, WireConvert.ToDatabase(new WireInteger(long.MinValue)));
        Assert.Equal(0.5, WireConvert.ToDatabase(new WireNumber(0.5)));
        Assert.Equal("text", WireConvert.ToDatabase(new WireString("text")));
    }

    [Fact]
    public void A_number_too_large_to_store_is_refused_in_plain_words()
    {
        var tooLarge = new WireInteger(new BigInteger(long.MaxValue) + 1);
        var tooSmall = new WireInteger(new BigInteger(long.MinValue) - 1);

        Assert.Equal("The number is too large to store.", Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(tooLarge)).Message);
        Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(tooSmall));
    }

    [Theory]
    [MemberData(nameof(KindsThatDoNotFitOneColumn))]
    public void A_list_or_an_object_cannot_be_stored_in_one_column(WireValue value, string kind) =>
        Assert.Equal($"Weir cannot store {kind} in a single database column.", Assert.Throws<WireTypeException>(() => WireConvert.ToDatabase(value)).Message);

    public static TheoryData<WireValue, string> KindsThatDoNotFitOneColumn() => new()
    {
        { new WireArray(), "a list" },
        { new WireObject(), "an object" },
        { new WireTimestampValue(default), "a date and time" },
    };
}
