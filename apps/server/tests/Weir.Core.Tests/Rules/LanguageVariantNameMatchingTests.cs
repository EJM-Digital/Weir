using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>How a track's name is matched against the variant markers: whole tokens only, separators folded, accents ignored.</summary>
public sealed class LanguageVariantNameMatchingTests
{
    [Theory]
    // A marker inside a longer word, or glued to a digit, is not the marker.
    [InlineData("chi", "chtx", null)]
    [InlineData("chi", "xcht", null)]
    [InlineData("chi", "cht2", null)]
    [InlineData("chi", "2cht", null)]
    // A rejected first occurrence does not hide a later whole-token one.
    [InlineData("chi", "chtx cht", "zho-Hant")]
    [InlineData("chi", "xcht cht", "zho-Hant")]
    // Dots, underscores and runs of spaces all read as one separator.
    [InlineData("fre", "Canadian_French", "fre-CA")]
    [InlineData("fre", "Canadian.French", "fre-CA")]
    [InlineData("fre", "Canadian   French", "fre-CA")]
    [InlineData("fre", "Canadian _ . French", "fre-CA")]
    // A title that is only a combining mark has nothing left to match once it is stripped.
    [InlineData("fre", "́", null)]
    public void A_marker_matches_only_as_a_whole_token(string baseCode, string title, string? expected) =>
        Assert.Equal(expected, LanguageVariants.DetectVariant(title, baseCode));

    [Fact]
    public void Marker_matching_ignores_accents_in_the_title()
    {
        Assert.Equal("fre-CA", LanguageVariants.DetectVariant("QUÉBÉCOIS", "fre"));
    }
}
