using Weir.Core.Updates;

namespace Weir.Core.Tests.Updates;

/// <summary>Comparing versions through the operators, equality and hashing a caller uses.</summary>
public sealed class SemanticVersionOperatorTests
{
    private static SemanticVersion Version(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version!;
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void Versions_order_by_precedence_through_every_operator(string left, string right, int expected)
    {
        var a = Version(left);
        var b = Version(right);

        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
        Assert.Equal(expected == 0, a == b);
        Assert.Equal(expected != 0, a != b);
        Assert.Equal(expected < 0, a < b);
        Assert.Equal(expected > 0, a > b);
        Assert.Equal(expected <= 0, a <= b);
        Assert.Equal(expected >= 0, a >= b);
        Assert.Equal(expected == 0, a.Equals((object)b));
        Assert.Equal(expected == 0, a.GetHashCode() == b.GetHashCode());
    }

    [Fact]
    public void A_missing_version_sorts_before_any_version()
    {
        var version = Version("1.0.0");
        SemanticVersion? none = null;

        Assert.Equal(1, version.CompareTo(none));
        Assert.True(none < version);
        Assert.False(version < none);
        Assert.True(version > none);
        Assert.False(none > version);
        SemanticVersion? other = null;
        Assert.False(none < other);
        Assert.True(none == null);
        Assert.True(none != version);
        Assert.False(version == none);
        Assert.False(version.Equals(none));
        Assert.False(version.Equals("1.0.0"));
    }
}
