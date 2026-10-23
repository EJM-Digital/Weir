using Weir.Core.Net;
using Weir.Core.Notifications;

namespace Weir.Core.Tests.Net;

/// <summary>The last edges of the host allow-list, network ranges and alert addresses: odd Host headers, an address of the wrong family, and fragments.</summary>
public sealed class NetRemainingEdgeTests
{
    private static readonly string[] None = [];

    [Fact]
    public void A_missing_host_header_is_not_allowed()
    {
        Assert.False(AllowedHostPolicy.IsAllowed(null!, None, None));
    }

    [Theory]
    [InlineData("2001:db8::1", true)]
    [InlineData("[::1", true)]
    [InlineData("[", true)]
    [InlineData("a:b:c.example", false)]
    public void A_host_header_with_brackets_or_several_colons_is_read_as_it_is_written(string header, bool allowed)
    {
        Assert.Equal(allowed, AllowedHostPolicy.IsAllowed(header, None, None));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void A_trusted_origin_that_has_no_host_allows_nothing(string origin)
    {
        Assert.False(AllowedHostPolicy.IsAllowed("weir.example.com", None, [origin]));
    }

    [Fact]
    public void A_trusted_origin_allows_its_own_host_and_no_other()
    {
        Assert.True(AllowedHostPolicy.IsAllowed("weir.example.com", None, ["https://weir.example.com:8443"]));
        Assert.False(AllowedHostPolicy.IsAllowed("other.example.com", None, ["https://weir.example.com:8443"]));
    }

    [Fact]
    public void A_network_of_the_wrong_address_family_is_refused()
    {
        Assert.Throws<ArgumentException>(() => NetRange.ParseV4("2001:db8::", 32));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV4("not an address", 24));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV6("192.0.2.0", 24));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV6("not an address", 64));
    }

    [Theory]
    [InlineData("example.com/hook")]
    [InlineData("https:///hook")]
    [InlineData("")]
    public void An_address_without_a_scheme_or_a_host_is_hidden_whole(string address)
    {
        var (shown, hadToken) = NotificationRules.MaskAddress(address);

        Assert.True(hadToken);
        Assert.Equal(NotificationRules.TokenMask, shown);
    }

    [Theory]
    [InlineData("https://user:pw@example.com/hook/secret", "https://example.com/hook/••••", true)]
    [InlineData("https://example.com", "https://example.com", false)]
    [InlineData("https://example.com/hook?token=1", "https://example.com/••••?••••", true)]
    [InlineData("https://example.com/#top", "https://example.com/", true)]
    public void A_valid_address_is_shown_with_its_secret_parts_hidden(string address, string shown, bool hidden)
    {
        Assert.Equal((shown, hidden), NotificationRules.MaskAddress(address));
    }

    [Fact]
    public void An_address_is_compared_with_its_fragment_kept()
    {
        Assert.Equal("https://example.com/hook?a=1#top", NotificationRules.NormalizeAddress(" https://Example.com/hook/?a=1#top "));
    }
}
