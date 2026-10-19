using Weir.Core.Json;

namespace Weir.Core.Tests.Json;

/// <summary>Text rules whose results appear in stored data and messages: whitespace, length and slicing by code point, and the quoted display form.</summary>
public sealed class WireStringsTests
{
    /// <summary>Text from UTF-16 code units, so the source stays plain ASCII and a lone surrogate can be written.</summary>
    private static string Units(params int[] codes) => string.Concat(codes.Select(code => (char)code));

    private static readonly string Smile = char.ConvertFromUtf32(0x1F600);

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0a)]
    [InlineData(0x0b)]
    [InlineData(0x0c)]
    [InlineData(0x0d)]
    [InlineData(0x20)]
    [InlineData(0x1c)]
    [InlineData(0x1f)]
    [InlineData(0x85)]
    [InlineData(0xa0)]
    [InlineData(0x1680)]
    [InlineData(0x2000)]
    [InlineData(0x200a)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x202f)]
    [InlineData(0x205f)]
    [InlineData(0x3000)]
    public void These_characters_are_whitespace(int code) => Assert.True(WireStrings.IsSpace((char)code));

    [Theory]
    [InlineData(0x61)]
    [InlineData(0x30)]
    [InlineData(0x1b)]
    [InlineData(0x21)]
    [InlineData(0x1fff)]
    [InlineData(0x200b)]
    [InlineData(0x2010)]
    [InlineData(0xfeff)]
    public void These_characters_are_not_whitespace(int code) => Assert.False(WireStrings.IsSpace((char)code));

    [Fact]
    public void Stripping_removes_whitespace_from_both_ends_only()
    {
        Assert.Equal("a b", WireStrings.Strip(Units(0x20, 0x09, 0xa0) + "a b" + Units(0x3000, 0x0a)));
        Assert.Equal(string.Empty, WireStrings.Strip(Units(0x20, 0x09, 0x2003, 0x20)));
        Assert.Equal(string.Empty, WireStrings.Strip(string.Empty));
        Assert.Equal("a" + Units(0x200b), WireStrings.Strip("a" + Units(0x200b)));
        Assert.Throws<ArgumentNullException>(() => WireStrings.Strip(null!));
    }

    [Fact]
    public void Length_counts_code_points_and_a_lone_surrogate_counts_as_one()
    {
        Assert.Equal(0, WireStrings.Length(string.Empty));
        Assert.Equal(3, WireStrings.Length("abc"));
        Assert.Equal(3, WireStrings.Length("a" + Smile + "b"));
        Assert.Equal(2, WireStrings.Length(Units(0xd800) + "a"));
        Assert.Equal(1, WireStrings.Length(Units(0xdc00)));
        Assert.Throws<ArgumentNullException>(() => WireStrings.Length(null!));
    }

    [Fact]
    public void Slicing_takes_whole_code_points_and_never_splits_a_pair()
    {
        Assert.Equal(string.Empty, WireStrings.Slice("abc", 0));
        Assert.Equal(string.Empty, WireStrings.Slice("abc", -1));
        Assert.Equal("abc", WireStrings.Slice("abc", 3));
        Assert.Equal("abc", WireStrings.Slice("abc", 10));
        Assert.Equal("ab", WireStrings.Slice("abc", 2));
        Assert.Equal("a" + Smile, WireStrings.Slice("a" + Smile + "bc", 2));
        Assert.Equal(Smile, WireStrings.Slice(Smile + Smile, 1));
        Assert.Equal(Smile + Smile, WireStrings.Slice(Smile + Smile, 3));
        Assert.Equal(Units(0xd800), WireStrings.Slice(Units(0xd800) + "a", 1));
        Assert.Throws<ArgumentNullException>(() => WireStrings.Slice(null!, 1));
    }

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("", "''")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("it's \"x\"", "'it\\'s \"x\"'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("a\tb\nc\rd", "'a\\tb\\nc\\rd'")]
    public void The_display_form_quotes_and_escapes_what_cannot_be_shown_as_it_is(string value, string expected) =>
        Assert.Equal(expected, WireStrings.Repr(value));

    [Theory]
    [InlineData(0x01, "\\x01")]
    [InlineData(0x1f, "\\x1f")]
    [InlineData(0x7f, "\\x7f")]
    [InlineData(0x85, "\\x85")]
    [InlineData(0xa0, "\\xa0")]
    [InlineData(0xad, "\\xad")]
    [InlineData(0x200b, "\\u200b")]
    [InlineData(0x2028, "\\u2028")]
    [InlineData(0xe000, "\\ue000")]
    [InlineData(0x0378, "\\u0378")]
    [InlineData(0xd800, "\\ud800")]
    public void A_character_that_is_not_printable_is_written_as_an_escape(int code, string escape) =>
        Assert.Equal("'" + escape + "'", WireStrings.Repr(Units(code)));

    [Fact]
    public void Printable_characters_beyond_ascii_are_shown_as_they_are_and_the_rest_of_the_astral_planes_are_escaped()
    {
        Assert.Equal("'" + Units(0xe9, 0x20, 0x4e2d) + "'", WireStrings.Repr(Units(0xe9, 0x20, 0x4e2d)));
        Assert.Equal("'" + Smile + "'", WireStrings.Repr(Smile));
        Assert.Equal("'\\U0010ffff'", WireStrings.Repr(char.ConvertFromUtf32(0x10ffff)));
    }

    [Fact]
    public void Text_to_show_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => WireStrings.Repr(null!));
    }
}
