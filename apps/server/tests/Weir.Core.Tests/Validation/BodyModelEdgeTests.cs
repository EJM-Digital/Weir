using Weir.Core.Json;
using Weir.Core.Validation;

namespace Weir.Core.Tests.Validation;

/// <summary>A body that is missing, is not an object, or carries integers beyond what a long can hold.</summary>
public sealed class BodyModelEdgeTests
{
    private static BodyModel Model(string? json, out ValidationIssues issues)
    {
        issues = new ValidationIssues();
        return new BodyModel(json is null ? null : WireJsonParser.Parse(json), issues);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void A_body_that_was_not_sent_is_reported_missing(string? json)
    {
        var model = Model(json, out var issues);

        Assert.False(model.IsPresent);
        Assert.False(model.IsValid);
        Assert.Equal("missing", Assert.Single(issues.All).Type);
        Assert.False(model.Has("anything"));
    }

    [Fact]
    public void A_body_that_is_not_an_object_is_reported_and_has_no_keys()
    {
        var model = Model("[1]", out var issues);

        Assert.True(model.IsPresent);
        Assert.False(model.IsValid);
        Assert.Equal("model_attributes_type", Assert.Single(issues.All).Type);
        Assert.False(model.Has("a"));
        model.Finish(ExtraFields.Forbid);
        Assert.Single(issues.All);
    }

    [Fact]
    public void A_key_sent_as_null_is_still_a_key()
    {
        var model = Model("""{"a":null}""", out _);

        Assert.True(model.Has("a"));
        Assert.False(model.Has("b"));
    }

    [Theory]
    [InlineData("99999999999999999999", long.MaxValue)]
    [InlineData("-99999999999999999999", long.MinValue)]
    public void An_integer_too_large_for_a_long_is_clamped(string digits, long expected)
    {
        var model = Model($$"""{"n":{{digits}},"o":{{digits}}}""", out _);

        Assert.Equal(expected, model.Number("n", 0, required: true));
        Assert.Equal(expected, model.OptionalInt("o"));
    }

    [Fact]
    public void Integer_lists_fall_back_to_their_default_when_the_key_is_absent()
    {
        var model = Model("""{"list":[1,2]}""", out _);

        Assert.Equal([1, 2], model.IntList("list", [9]));
        Assert.Equal([9], model.IntList("absent", [9]));
        Assert.Empty(model.IntList("absent"));
    }
}
