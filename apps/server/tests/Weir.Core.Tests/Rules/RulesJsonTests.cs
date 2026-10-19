using System.Text.Json;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>The loose conversions the rules apply to ffprobe's JSON, including which inputs are refused and with what wording.</summary>
public sealed class RulesJsonTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static RulesInputException Refused(Action action) => Assert.Throws<RulesInputException>(action);

    [Fact]
    public void Text_is_lower_and_upper_cased_the_same_way_on_every_machine()
    {
        Assert.Equal("title", RulesJson.Lower("TITLE"));
        Assert.Equal("TITLE", RulesJson.Upper("title"));
        Assert.Equal("i", RulesJson.Lower("I"));
    }

    [Fact]
    public void A_value_is_read_by_name_and_the_last_of_a_repeated_name_wins()
    {
        var obj = Json("""{"a": 1, "b": 2, "a": 3}""");

        Assert.Equal(3, RulesJson.Get(obj, "a")!.Value.GetInt32());
        Assert.Null(RulesJson.Get(obj, "missing"));
        Assert.Equal(2, RulesJson.Item(obj, "b").GetInt32());
        var missing = Refused(() => RulesJson.Item(obj, "missing"));
        Assert.Equal("KeyError", missing.ErrorKind);
        Assert.Equal("'missing'", missing.Message);
    }

    [Fact]
    public void The_entries_of_an_object_keep_the_first_position_of_a_name_and_its_last_value()
    {
        var items = RulesJson.Items(Json("""{"a": 1, "b": 2, "a": 3}"""));

        Assert.Equal(["a", "b"], items.Select(item => item.Key));
        Assert.Equal([3, 2], items.Select(item => item.Value.GetInt32()));
    }

    [Fact]
    public void The_kind_of_a_value_is_told_apart()
    {
        Assert.True(RulesJson.IsNone(null));
        Assert.True(RulesJson.IsNone(Json("null")));
        Assert.False(RulesJson.IsNone(Json("0")));
        Assert.True(RulesJson.IsDict(Json("{}")));
        Assert.False(RulesJson.IsDict(Json("[]")));
        Assert.False(RulesJson.IsDict(null));
        Assert.True(RulesJson.IsList(Json("[]")));
        Assert.False(RulesJson.IsList(Json("{}")));
        Assert.True(RulesJson.IsStr(Json("\"x\"")));
        Assert.False(RulesJson.IsStr(Json("1")));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"0\"", true)]
    [InlineData("0", false)]
    [InlineData("7", true)]
    [InlineData("-1", true)]
    [InlineData("0.0", false)]
    [InlineData("0.5", true)]
    [InlineData("0e0", false)]
    [InlineData("2E3", true)]
    [InlineData("[]", false)]
    [InlineData("[0]", true)]
    [InlineData("{}", false)]
    [InlineData("{\"a\": 0}", true)]
    [InlineData("100000000000000000000000", true)]
    public void A_value_counts_as_set_unless_it_is_nothing_zero_or_empty(string json, bool expected) =>
        Assert.Equal(expected, RulesJson.Truthy(Json(json)));

    [Fact]
    public void A_missing_or_undefined_value_does_not_count_as_set()
    {
        Assert.False(RulesJson.Truthy(null));
        Assert.False(RulesJson.Truthy(default(JsonElement)));
    }

    [Fact]
    public void Text_falls_back_when_the_value_is_not_set()
    {
        Assert.Equal("fallback", RulesJson.StrOr(null, "fallback"));
        Assert.Equal("fallback", RulesJson.StrOr(Json("\"\""), "fallback"));
        Assert.Equal("x", RulesJson.StrOr(Json("\"x\""), "fallback"));
        Assert.Equal("7", RulesJson.StrOr(Json("7"), "fallback"));
    }

    [Fact]
    public void Text_to_strip_is_empty_for_a_value_that_is_not_set_and_refused_when_it_is_set_but_not_text()
    {
        Assert.Equal(string.Empty, RulesJson.StrMethodTarget(null));
        Assert.Equal(string.Empty, RulesJson.StrMethodTarget(Json("0")));
        Assert.Equal("abc", RulesJson.StrMethodTarget(Json("\"abc\"")));
        var error = Refused(() => RulesJson.StrMethodTarget(Json("5")));
        Assert.Equal("AttributeError", error.ErrorKind);
        Assert.Equal("Expected text but found a whole number.", error.Message);
        Assert.Equal("Expected text but found a decimal number.", Refused(() => RulesJson.StrMethodTarget(Json("1.5"))).Message);
        Assert.Equal("Expected text but found true or false.", Refused(() => RulesJson.StrMethodTarget(Json("true"))).Message);
        Assert.Equal("Expected text but found a list.", Refused(() => RulesJson.StrMethodTarget(Json("[1]"))).Message);
        Assert.Equal("Expected text but found an object.", Refused(() => RulesJson.StrMethodTarget(Json("{\"a\": 1}"))).Message);
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("12", 12)]
    [InlineData("-12", -12)]
    [InlineData("3.9", 3)]
    [InlineData("-3.9", -3)]
    [InlineData("1e3", 1000)]
    [InlineData("\"  42 \"", 42)]
    [InlineData("\"-7\"", -7)]
    public void A_value_is_read_as_a_whole_number_the_way_the_golden_files_expect(string json, long expected) =>
        Assert.Equal(expected, RulesJson.Int(Json(json)));

    [Theory]
    [InlineData(null, "TypeError", "Expected a whole number but found nothing.")]
    [InlineData("null", "TypeError", "Expected a whole number but found nothing.")]
    [InlineData("[1]", "TypeError", "Expected a whole number but found a list.")]
    [InlineData("{}", "TypeError", "Expected a whole number but found an object.")]
    [InlineData("\"abc\"", "ValueError", "'abc' is not a whole number.")]
    [InlineData("\"1.5\"", "ValueError", "'1.5' is not a whole number.")]
    [InlineData("99999999999999999999", "OverflowError", "integer is outside the range the .NET engine supports")]
    [InlineData("1e30", "OverflowError", "integer is outside the range the .NET engine supports")]
    public void A_value_that_is_not_a_whole_number_is_refused_with_its_error_kind_and_wording(string? json, string kind, string message)
    {
        var error = Refused(() => RulesJson.Int(json is null ? null : Json(json)));

        Assert.Equal(kind, error.ErrorKind);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void An_undefined_value_is_nothing()
    {
        Assert.Equal("Expected a whole number but found nothing.", Refused(() => RulesJson.Int(default(JsonElement))).Message);
    }

    [Fact]
    public void Trying_a_whole_number_gives_up_on_the_wrong_type_or_text_but_not_on_an_overflow()
    {
        Assert.True(RulesJson.TryInt(Json("\"5\""), out var five));
        Assert.Equal(5, five);
        Assert.False(RulesJson.TryInt(Json("[]"), out var list));
        Assert.Equal(0, list);
        Assert.False(RulesJson.TryInt(Json("\"x\""), out _));
        Assert.False(RulesJson.TryInt(null, out _));
        Assert.Equal("OverflowError", Refused(() => RulesJson.TryInt(Json("99999999999999999999"), out _)).ErrorKind);
    }

    [Fact]
    public void Text_is_read_as_a_whole_number_after_stripping_the_space_around_it()
    {
        Assert.Equal(12, RulesJson.IntFromText(" 12 "));
        Assert.Equal(12, RulesJson.TryIntFromText("12"));
        Assert.Null(RulesJson.TryIntFromText("twelve"));
        Assert.Equal("'twelve' is not a whole number.", Refused(() => RulesJson.IntFromText("twelve")).Message);
    }

    [Fact]
    public void A_number_is_narrowed_to_32_bits_only_when_it_fits()
    {
        Assert.Equal(int.MaxValue, RulesJson.ToInt32(int.MaxValue));
        Assert.Equal(int.MinValue, RulesJson.ToInt32(int.MinValue));
        Assert.Equal("OverflowError", Refused(() => RulesJson.ToInt32(int.MaxValue + 1L)).ErrorKind);
        Assert.Equal("OverflowError", Refused(() => RulesJson.ToInt32(int.MinValue - 1L)).ErrorKind);
    }

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData(" 1.5 ", 1.5)]
    [InlineData("-1.5", -1.5)]
    [InlineData("+2", 2.0)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5.0)]
    [InlineData("1_000.5", 1000.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1E-2", 0.01)]
    [InlineData("1.5e+2", 150.0)]
    [InlineData("-.5e1", -5.0)]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("-INF", double.NegativeInfinity)]
    [InlineData("+Infinity", double.PositiveInfinity)]
    public void Text_is_read_as_a_decimal_number_in_every_form_ffprobe_writes(string text, double expected) =>
        Assert.Equal(expected, RulesJson.TryFloatFromText(text));

    [Fact]
    public void The_text_nan_is_not_a_number_in_any_case()
    {
        Assert.True(double.IsNaN(RulesJson.TryFloatFromText("NaN")!.Value));
        Assert.True(double.IsNaN(RulesJson.TryFloatFromText("-nan")!.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("1._5")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e1.5")]
    [InlineData("e5")]
    [InlineData("1,5")]
    [InlineData("0x10")]
    [InlineData("infinit")]
    public void Text_that_is_not_a_decimal_number_is_not_read(string text) =>
        Assert.Null(RulesJson.TryFloatFromText(text));

    [Fact]
    public void A_value_is_shown_as_text_in_the_golden_files_form()
    {
        Assert.Equal("None", RulesJson.Str(null));
        Assert.Equal("plain", RulesJson.Str(Json("\"plain\"")));
        Assert.Equal("None", RulesJson.Str(Json("null")));
        Assert.Equal("True", RulesJson.Str(Json("true")));
        Assert.Equal("False", RulesJson.Str(Json("false")));
        Assert.Equal("12", RulesJson.Str(Json("12")));
        Assert.Equal("1.5", RulesJson.Str(Json("1.5")));
        Assert.Equal("1e+22", RulesJson.Str(Json("1e22")));
        Assert.Equal("100000000000000000000000", RulesJson.Str(Json("100000000000000000000000")));
        Assert.Equal("[1, 'a', None]", RulesJson.Str(Json("[1, \"a\", null]")));
        Assert.Equal("{'a': 1, 'b': [True]}", RulesJson.Str(Json("""{"a": 1, "b": [true], "a": 1}""")));
        Assert.Equal("None", RulesJson.Repr(default(JsonElement)));
        Assert.Equal("\"it's\"", RulesJson.Repr("it's"));
        Assert.Equal("'plain'", RulesJson.Repr("plain"));
    }
}
