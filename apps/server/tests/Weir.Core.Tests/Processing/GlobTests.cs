using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>The shell-style patterns a workflow's include and exclude lists accept.</summary>
public sealed class GlobTests
{
    [Theory]
    [InlineData("Heat.mkv", "Heat.mkv", true)]
    [InlineData("Heat.mkv", "heat.mkv", false)]
    [InlineData("Heat.mkv", "Heat.mk", false)]
    [InlineData("Heat.mk", "Heat.mkv", false)]
    [InlineData("", "", true)]
    [InlineData("", "?", false)]
    public void Plain_text_must_match_exactly(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(value, pattern));
    }

    [Theory]
    [InlineData("Heat.mkv", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("Heat.mkv", "H*", true)]
    [InlineData("Heat.mkv", "*.mkv", true)]
    [InlineData("Heat.mkv", "H**v", true)]
    [InlineData("Heat.mkv", "*e*k*", true)]
    [InlineData("Heat.mkv", "*.mp4", false)]
    [InlineData("Heat.mkv", "*x", false)]
    public void A_star_stands_for_any_run_of_characters(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(value, pattern));
    }

    [Theory]
    [InlineData("Heat.mkv", "Hea?.mkv", true)]
    [InlineData("Hea.mkv", "Hea?.mkv", false)]
    public void A_question_mark_stands_for_one_character(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(value, pattern));
    }

    [Theory]
    [InlineData("b", "[abc]", true)]
    [InlineData("d", "[abc]", false)]
    [InlineData("b", "[a-c]", true)]
    [InlineData("d", "[a-c]", false)]
    [InlineData("d", "[!a-c]", true)]
    [InlineData("b", "[!a-c]", false)]
    [InlineData("d", "[^abc]", true)]
    [InlineData("a", "[^abc]", false)]
    [InlineData("-", "[a-]", true)]
    [InlineData("b", "[a-]", false)]
    [InlineData("x5", "x[0-9]", true)]
    public void A_bracket_stands_for_one_character_from_a_set_or_range(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(value, pattern));
    }

    [Theory]
    [InlineData("[a", "[a", true)]
    [InlineData("xa", "[a", false)]
    public void An_opening_bracket_with_no_closing_one_is_matched_as_itself(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(value, pattern));
    }
}
