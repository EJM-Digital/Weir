using Weir.Core.Json;
using Weir.Core.Validation;

namespace Weir.Core.Tests.Validation;

/// <summary>What the field readers say about the odd values a client can send: non-finite numbers, huge integers, wrong list items, malformed ids.</summary>
public sealed class FieldRulesEdgeTests
{
    private static readonly object[] Loc = ["body", "field"];

    private static WireValue Parse(string json) => WireJsonParser.Parse(json);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_number_that_is_not_finite_is_not_an_integer(double value)
    {
        var issues = new ValidationIssues();

        Assert.False(FieldRules.TryInt(new WireNumber(value), Loc, null, null, issues, out _));

        var issue = Assert.Single(issues.All);
        Assert.Equal(("finite_number", "Input should be a finite number"), (issue.Type, issue.Msg));
    }

    [Theory]
    [InlineData("""["only"]""", "Input should be 'only'")]
    [InlineData("""["a","b"]""", "Input should be 'a' or 'b'")]
    [InlineData("""["a","b","c"]""", "Input should be 'a', 'b' or 'c'")]
    public void A_value_outside_the_allowed_words_names_them_all(string allowedJson, string expected)
    {
        var allowed = ((WireArray)Parse(allowedJson)).Items.Cast<WireString>().Select(item => item.Value).ToList();
        var issues = new ValidationIssues();

        Assert.False(FieldRules.TryLiteral(new WireString("other"), Loc, allowed, issues, out _));
        Assert.False(FieldRules.TryLiteral(new WireInteger(3), Loc, allowed, issues, out _));

        Assert.All(issues.All, issue => Assert.Equal(expected, issue.Msg));
        Assert.Equal(2, issues.All.Count);
    }

    [Fact]
    public void A_list_of_text_reports_each_item_that_is_not_text()
    {
        var issues = new ValidationIssues();

        Assert.False(FieldRules.TryStrList(Parse("""["a",1,"b",null]"""), Loc, issues, out var kept));

        Assert.Equal(["a", "b"], kept);
        Assert.Equal([new object[] { "body", "field", 1 }, new object[] { "body", "field", 3 }], issues.All.Select(issue => issue.Loc.ToArray()));
    }

    [Fact]
    public void Something_that_is_not_a_list_is_not_a_list_of_text_or_of_integers()
    {
        var issues = new ValidationIssues();

        Assert.False(FieldRules.TryStrList(Parse("""{}"""), Loc, issues, out _));
        Assert.False(FieldRules.TryIntList(Parse("""3"""), Loc, issues, out _));

        Assert.All(issues.All, issue => Assert.Equal("list_type", issue.Type));
    }

    [Fact]
    public void A_list_of_integers_is_clamped_to_what_fits_and_reports_each_bad_item()
    {
        var issues = new ValidationIssues();

        Assert.False(FieldRules.TryIntList(Parse("""[1, 99999999999999999999, -99999999999999999999, "x", true]"""), Loc, issues, out var kept));

        Assert.Equal([1, long.MaxValue, long.MinValue, 1], kept);
        Assert.Single(issues.All);
    }

    [Theory]
    [InlineData("5", true)]
    [InlineData(" 5.0 ", true)]
    [InlineData("5.000", true)]
    [InlineData("5.", false)]
    [InlineData("5.5", false)]
    [InlineData("five", false)]
    public void Text_is_read_as_an_integer_only_when_it_holds_one(string text, bool accepted) =>
        Assert.Equal(accepted, FieldRules.TryInt(new WireString(text), Loc, null, null, new ValidationIssues(), out _));

    [Fact]
    public void A_date_time_may_be_null()
    {
        var issues = new ValidationIssues();

        Assert.True(FieldRules.TryDateTime(WireNull.Instance, Loc, issues, out var value));

        Assert.Null(value);
        Assert.False(issues.Any);
    }

    [Theory]
    [InlineData("12345678-1234-1234-1234-123456789012", null)]
    [InlineData("{12345678-1234-1234-1234-123456789012}", null)]
    [InlineData("urn:uuid:12345678-1234-1234-1234-123456789012", null)]
    [InlineData("12345678123412341234123456789012", null)]
    [InlineData("1234567-81234-1234-1234-123456789012", "invalid group length in group 0: expected 8, found 7")]
    [InlineData("12345678-123-41234-1234-123456789012", "invalid group length in group 1: expected 4, found 3")]
    [InlineData("12345678-1234-1234-1234-1234567890123", "invalid group length in group 4: expected 12, found 13")]
    [InlineData("12345678-1234-1234-1234", "invalid group count: expected 5, found 4")]
    [InlineData("12345678-1234-1234-1234-1234-123456789012", "invalid group count: expected 5, found 6")]
    [InlineData("1234", "invalid length: expected length 32 for simple format, found 4")]
    [InlineData("1234567g-1234-1234-1234-123456789012", "invalid character: found `g` at 8")]
    public void A_uuid_is_accepted_in_every_spelling_and_refused_with_the_exact_reason(string text, string? error)
    {
        var issues = new ValidationIssues();

        var accepted = FieldRules.TryUuid(text, Loc, issues, out _);

        Assert.Equal(error is null, accepted);
        if (error is not null)
        {
            Assert.Equal("Input should be a valid UUID, " + error, Assert.Single(issues.All).Msg);
        }
    }

    [Fact]
    public void Issues_are_thrown_together_in_the_order_they_were_found()
    {
        var issues = new ValidationIssues();
        issues.ThrowIfAny();
        issues.Add(new ValidationIssue("missing", Loc, "Field required", WireNull.Instance));
        issues.Add(new ValidationIssue("string_type", [.. Loc, 2], "Input should be a valid string", WireValue.Of(1)));

        var error = Assert.Throws<RequestValidationException>(issues.ThrowIfAny);

        Assert.Equal(["missing", "string_type"], error.Issues.Select(issue => issue.Type));
        Assert.Equal(2, ((WireArray)error.ToBody()["detail"]).Items.Count);
    }

    [Fact]
    public void A_validation_error_can_be_made_with_a_message_and_a_cause()
    {
        var cause = new InvalidOperationException("inner");

        Assert.Equal("plain", new RequestValidationException("plain").Message);
        Assert.Same(cause, new RequestValidationException("wrapped", cause).InnerException);
        Assert.Empty(new RequestValidationException().Issues);
        Assert.Empty(new RequestValidationException("plain").Issues);
        Assert.Empty(new RequestValidationException("wrapped", cause).Issues);
    }
}
