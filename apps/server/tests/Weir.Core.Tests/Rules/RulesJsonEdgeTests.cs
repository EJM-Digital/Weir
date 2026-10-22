using System.Text.Json;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>How the rules engine reads the loosely typed values ffprobe reports: which count as set, what they print as, and how odd ones are refused.</summary>
public sealed class RulesJsonEdgeTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData("\"\"", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("0.5", true)]
    [InlineData("\"x\"", true)]
    [InlineData("[0]", true)]
    [InlineData("{\"a\": 0}", true)]
    public void A_value_counts_as_set_unless_it_is_zero_empty_false_or_null(string json, bool expected)
    {
        Assert.Equal(expected, RulesJson.Truthy(J(json)));
    }

    [Fact]
    public void A_value_that_is_absent_counts_as_not_set_and_prints_as_none()
    {
        Assert.False(RulesJson.Truthy(null));
        Assert.False(RulesJson.Truthy(default(JsonElement)));
        Assert.Equal("None", RulesJson.Str(null));
        Assert.Equal("None", RulesJson.Repr(default(JsonElement)));
        Assert.Equal("None", RulesJson.Repr(J("null")));
    }

    [Theory]
    [InlineData("true", "True")]
    [InlineData("false", "False")]
    [InlineData("7", "7")]
    [InlineData("1.5", "1.5")]
    [InlineData("[1, \"a\"]", "[1, 'a']")]
    [InlineData("{\"k\": [true]}", "{'k': [True]}")]
    public void A_value_prints_the_way_it_is_written_in_error_messages(string json, string expected)
    {
        Assert.Equal(expected, RulesJson.Repr(J(json)));
    }

    [Theory]
    [InlineData("true", "Expected text but found true or false.")]
    [InlineData("5", "Expected text but found a whole number.")]
    [InlineData("1.5", "Expected text but found a decimal number.")]
    [InlineData("[1]", "Expected text but found a list.")]
    [InlineData("{\"a\": 1}", "Expected text but found an object.")]
    public void A_value_that_should_be_text_says_what_it_was_instead(string json, string message)
    {
        var thrown = Assert.Throws<RulesInputException>(() => RulesJson.StrMethodTarget(J(json)));

        Assert.Equal(("AttributeError", message), (thrown.ErrorKind, thrown.Message));
    }

    [Fact]
    public void A_value_that_is_not_set_reads_as_empty_text()
    {
        Assert.Equal(string.Empty, RulesJson.StrMethodTarget(null));
        Assert.Equal(string.Empty, RulesJson.StrMethodTarget(J("0")));
        Assert.Equal("eng", RulesJson.StrMethodTarget(J("\"eng\"")));
    }

    [Theory]
    [InlineData("[1]", "Expected a whole number but found a list.")]
    [InlineData("{\"a\": 1}", "Expected a whole number but found an object.")]
    public void A_list_or_object_is_not_a_whole_number(string json, string message)
    {
        var thrown = Assert.Throws<RulesInputException>(() => RulesJson.Int(J(json)));

        Assert.Equal(("TypeError", message), (thrown.ErrorKind, thrown.Message));
    }

    [Fact]
    public void Nothing_is_not_a_whole_number()
    {
        Assert.Equal("Expected a whole number but found nothing.", Assert.Throws<RulesInputException>(() => RulesJson.Int(default(JsonElement))).Message);
        Assert.False(RulesJson.TryInt(J("null"), out _));
    }

    [Theory]
    [InlineData("99999999999999999999")]
    [InlineData("-99999999999999999999")]
    [InlineData("1e30")]
    [InlineData("-1e30")]
    public void A_whole_number_too_large_to_hold_is_refused_as_an_overflow(string json)
    {
        var thrown = Assert.Throws<RulesInputException>(() => RulesJson.Int(J(json)));

        Assert.Equal("OverflowError", thrown.ErrorKind);
        Assert.Throws<RulesInputException>(() => RulesJson.TryInt(J(json), out _));
    }

    [Theory]
    [InlineData("1_000.5", 1000.5)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5.0)]
    [InlineData("1e3", 1000.0)]
    [InlineData("2.5E+2", 250.0)]
    [InlineData("-1_0e-1", -1.0)]
    [InlineData("+3", 3.0)]
    public void Text_in_the_float_grammar_is_read_as_a_number(string text, double expected)
    {
        Assert.Equal(expected, RulesJson.TryFloatFromText(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("_1")]
    [InlineData("1__0")]
    [InlineData("1_")]
    [InlineData("1.2.3")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("e5")]
    [InlineData("abc")]
    public void Text_outside_the_float_grammar_is_not_a_number(string text)
    {
        Assert.Null(RulesJson.TryFloatFromText(text));
    }

    [Fact]
    public void The_words_for_infinity_and_not_a_number_are_read_in_any_case()
    {
        Assert.Equal(double.PositiveInfinity, RulesJson.TryFloatFromText(" INF "));
        Assert.Equal(double.NegativeInfinity, RulesJson.TryFloatFromText("-Infinity"));
        Assert.True(double.IsNaN(RulesJson.TryFloatFromText("NaN")!.Value));
    }

    [Fact]
    public void A_decimal_number_too_large_for_a_double_is_refused_as_a_rules_input_error()
    {
        var thrown = Assert.Throws<RulesInputException>(() => RulesJson.Int(J("1e999")));

        Assert.Equal(("OverflowError", "cannot convert float infinity to integer"), (thrown.ErrorKind, thrown.Message));
    }
}
