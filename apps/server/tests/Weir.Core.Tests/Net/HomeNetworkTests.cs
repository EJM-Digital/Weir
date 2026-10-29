using Weir.Core.Json;
using Weir.Core.Net;

namespace Weir.Core.Tests.Net;

/// <summary>Which clients count as being on the home network, and which address a request is judged on.</summary>
public sealed class HomeNetworkTests
{
    private static readonly NetRange[] NoNetworks = [];
    private static readonly NetRange[] Proxies = [NetRange.ParseV4("10.0.0.2", 32), NetRange.ParseV4("172.16.0.0", 12)];

    private static NetAddress Address(string text) =>
        NetAddress.TryParse(text, out var address) ? address : throw new ArgumentException(text);

    private static HomeNetworkSettings On(string[]? extra = null, string[]? allow = null) => new(true, extra ?? [], allow ?? []);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("::ffff:192.168.1.20")]
    [InlineData("::ffff:127.0.0.1")]
    public void Every_listed_range_counts_as_local(string text) =>
        Assert.True(HomeNetwork.IsLocal(Address(text), NoNetworks));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.5")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("169.253.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fec0::1")]
    [InlineData("fbff::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::")]
    public void A_public_or_shared_address_never_counts_as_local(string text) =>
        Assert.False(HomeNetwork.IsLocal(Address(text), NoNetworks));

    [Fact]
    public void An_owner_added_network_counts_as_local()
    {
        NetRange[] extra = [NetRange.ParseV4("100.64.0.0", 10), NetRange.ParseV4("203.0.113.9", 32)];

        Assert.True(HomeNetwork.IsLocal(Address("100.64.7.7"), extra));
        Assert.True(HomeNetwork.IsLocal(Address("203.0.113.9"), extra));
        Assert.False(HomeNetwork.IsLocal(Address("203.0.113.10"), extra));
    }

    [Fact]
    public void Nothing_skips_while_the_setting_is_off_or_no_client_is_known()
    {
        Assert.False(HomeNetwork.Skips(Address("192.168.1.20"), HomeNetworkSettings.Off));
        Assert.False(HomeNetwork.Skips(null, On()));
    }

    [Fact]
    public void An_empty_allow_list_lets_every_local_address_skip_and_never_a_public_one()
    {
        Assert.True(HomeNetwork.Skips(Address("192.168.1.20"), On()));
        Assert.True(HomeNetwork.Skips(Address("::1"), On()));
        Assert.False(HomeNetwork.Skips(Address("8.8.8.8"), On()));
        Assert.False(HomeNetwork.Skips(Address("2001:4860:4860::8888"), On()));
    }

    [Fact]
    public void A_public_address_is_not_skipped_even_when_the_allow_list_names_it()
    {
        Assert.False(HomeNetwork.Skips(Address("8.8.8.8"), On(allow: ["8.8.8.8"])));
        Assert.False(HomeNetwork.Skips(Address("8.8.8.8"), On(allow: ["0.0.0.0/0"])));
    }

    [Fact]
    public void An_allow_list_narrows_the_local_addresses_that_skip()
    {
        var settings = On(allow: ["192.168.1.0/28", "10.0.0.7"]);

        Assert.True(HomeNetwork.Skips(Address("192.168.1.9"), settings));
        Assert.True(HomeNetwork.Skips(Address("10.0.0.7"), settings));
        Assert.False(HomeNetwork.Skips(Address("192.168.1.20"), settings));
        Assert.False(HomeNetwork.Skips(Address("10.0.0.8"), settings));
    }

    [Fact]
    public void An_owner_added_network_skips_only_when_the_allow_list_also_lets_it()
    {
        Assert.True(HomeNetwork.Skips(Address("100.64.7.7"), On(extra: ["100.64.0.0/10"])));
        Assert.False(HomeNetwork.Skips(Address("100.64.7.7"), On(extra: ["100.64.0.0/10"], allow: ["192.168.0.0/16"])));
        Assert.True(HomeNetwork.Skips(Address("100.64.7.7"), On(extra: ["100.64.0.0/10"], allow: ["100.64.7.0/24"])));
    }

    [Fact]
    public void A_stored_entry_that_cannot_be_read_never_widens_who_skips()
    {
        Assert.False(HomeNetwork.Skips(Address("100.64.7.7"), On(extra: ["not an address"])));
        Assert.False(HomeNetwork.Skips(Address("192.168.1.20"), On(allow: ["not an address"])));
    }

    [Fact]
    public void An_ipv4_mapped_address_is_judged_as_the_ipv4_address()
    {
        var settings = On(allow: ["192.168.1.0/24"]);

        Assert.True(HomeNetwork.Skips(Address("::ffff:192.168.1.20"), settings));
        Assert.False(HomeNetwork.Skips(Address("::ffff:192.168.2.20"), settings));
    }

    [Fact]
    public void Without_a_trusted_proxy_the_socket_peer_is_the_client_and_the_header_is_ignored()
    {
        var client = HomeNetwork.ClientOf("203.0.113.5", "192.168.1.20", NoNetworks);

        Assert.Equal(Address("203.0.113.5"), client);
    }

    [Fact]
    public void A_peer_that_is_not_a_trusted_proxy_cannot_hand_over_a_local_address_in_the_header()
    {
        var client = HomeNetwork.ClientOf("203.0.113.5", "192.168.1.20, 10.0.0.2", Proxies);

        Assert.Equal(Address("203.0.113.5"), client);
    }

    [Fact]
    public void A_trusted_proxy_hands_over_the_client_it_saw()
    {
        Assert.Equal(Address("203.0.113.5"), HomeNetwork.ClientOf("10.0.0.2", "203.0.113.5", Proxies));
        Assert.Equal(Address("192.168.1.20"), HomeNetwork.ClientOf("10.0.0.2", "192.168.1.20", Proxies));
    }

    [Fact]
    public void The_header_is_walked_from_the_right_and_stops_at_the_first_hop_that_is_not_a_trusted_proxy()
    {
        Assert.Equal(Address("203.0.113.5"), HomeNetwork.ClientOf("10.0.0.2", "192.168.1.20, 203.0.113.5, 172.16.4.4", Proxies));
        Assert.Equal(Address("203.0.113.5"), HomeNetwork.ClientOf("10.0.0.2", "192.168.1.20, 203.0.113.5", Proxies));
        Assert.Equal(Address("192.168.1.20"), HomeNetwork.ClientOf("10.0.0.2", "8.8.8.8, 192.168.1.20, 172.16.4.4", Proxies));
    }

    [Fact]
    public void A_forged_left_hand_hop_never_beats_the_hop_the_trusted_proxy_added()
    {
        var client = HomeNetwork.ClientOf("10.0.0.2", "127.0.0.1, 203.0.113.5", Proxies);

        Assert.Equal(Address("203.0.113.5"), client);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not an address")]
    [InlineData("192.168.1.20, not an address")]
    [InlineData("192.168.1.20:4000")]
    [InlineData("10.0.0.2, 172.16.4.4")]
    public void A_trusted_proxy_that_does_not_name_a_usable_client_leaves_the_client_unknown(string? forwardedFor) =>
        Assert.Null(HomeNetwork.ClientOf("10.0.0.2", forwardedFor, Proxies));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    public void A_peer_that_cannot_be_read_leaves_the_client_unknown(string? peer) =>
        Assert.Null(HomeNetwork.ClientOf(peer, "192.168.1.20", Proxies));

    [Fact]
    public void Entries_are_trimmed_unique_and_blank_ones_dropped()
    {
        var clean = HomeNetwork.CleanEntries([" 192.168.1.0/28 ", "", "  ", "192.168.1.0/28", "fd00::/8", "10.0.0.5"]);

        Assert.Equal(["192.168.1.0/28", "fd00::/8", "10.0.0.5"], clean);
    }

    [Fact]
    public void No_entries_is_an_empty_list() => Assert.Empty(HomeNetwork.CleanEntries(null));

    [Fact]
    public void A_bad_entry_is_refused_in_the_exact_words()
    {
        var problem = Assert.Throws<WireValueException>(() => HomeNetwork.CleanEntries(["192.168.1.0/28", "banana", "300.1.1.1"]));

        Assert.Equal(
            "'banana', '300.1.1.1' is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28).",
            problem.Message);
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, false, HomeNetworkWords.AllowedHostsOff)]
    public void The_allowed_hosts_warning_is_raised_only_when_the_skip_is_on_and_allowed_hosts_is_off(bool enabled, bool allowedHostsOn, string? expected)
    {
        var settings = enabled ? On() : HomeNetworkSettings.Off;

        Assert.Equal(expected, HomeNetworkWords.Warning(settings, allowedHostsOn));
    }

    [Fact]
    public void The_warning_reads_exactly_as_the_owner_asked() =>
        Assert.Equal(
            "Allowed Hosts (above) is off. Weir currently answers on any address, so any device that can reach a local address here skips sign-in too. Turn on Allowed Hosts as well for this to only apply on your own network.",
            HomeNetworkWords.AllowedHostsOff);

    [Fact]
    public void The_activity_words_follow_the_value()
    {
        Assert.Equal("Skip sign-in for devices on the local network turned on.", HomeNetworkWords.ActivityDetail(true));
        Assert.Equal("Skip sign-in for devices on the local network turned off.", HomeNetworkWords.ActivityDetail(false));
        Assert.Equal("Local network sign-in setting changed", HomeNetworkWords.ActivityTitle);
    }
}
