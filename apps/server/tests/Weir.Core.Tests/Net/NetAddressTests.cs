using System.Net;
using System.Numerics;
using Weir.Core.Net;

namespace Weir.Core.Tests.Net;

/// <summary>Strict address and network parsing, and the private, reserved and global classification the proxy and address checks rely on.</summary>
public sealed class NetAddressTests
{
    private static NetAddress Address(string text)
    {
        Assert.True(NetAddress.TryParse(text, out var address), $"{text} should parse.");
        return address;
    }

    private static NetRange Network(string text, bool strict = false)
    {
        Assert.True(NetRange.TryParse(text, strict, out var network), $"{text} should parse.");
        return network;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3.256")]
    [InlineData("1.2.3.04")]
    [InlineData("1.2.3.0004")]
    [InlineData("1.2.3.")]
    [InlineData("1.2.3.x")]
    [InlineData("fe80::1%")]
    [InlineData("fe80::1%eth/0")]
    [InlineData("::g")]
    [InlineData("1:2:3")]
    [InlineData("1.2.3.4:80")]
    public void Text_that_is_not_an_address_is_not_read(string? text)
    {
        Assert.False(NetAddress.TryParse(text, out var address));
        Assert.True(address.IsUnspecified);
        Assert.False(address.IsV6);
    }

    [Fact]
    public void A_dotted_quad_is_an_ipv4_address_of_32_bits()
    {
        var address = Address("192.168.1.20");

        Assert.False(address.IsV6);
        Assert.Equal(32, address.Bits);
        Assert.Equal(new BigInteger(0xC0A80114), address.Value);
    }

    [Fact]
    public void An_ipv6_address_is_128_bits_and_a_scope_is_kept_out_of_the_value()
    {
        var scoped = Address("fe80::1%eth0");

        Assert.True(scoped.IsV6);
        Assert.Equal(128, scoped.Bits);
        Assert.Equal(Address("fe80::1"), scoped);
    }

    [Fact]
    public void An_address_made_from_a_framework_address_has_its_family_and_value()
    {
        Assert.Equal(Address("10.1.2.3"), NetAddress.FromIpAddress(IPAddress.Parse("10.1.2.3")));
        Assert.Equal(Address("2001:db8::7"), NetAddress.FromIpAddress(IPAddress.Parse("2001:db8::7")));
        Assert.Throws<ArgumentNullException>(() => NetAddress.FromIpAddress(null!));
    }

    [Fact]
    public void An_ipv4_address_inside_an_ipv6_one_is_found_and_only_then()
    {
        Assert.Equal(Address("10.0.0.1"), Address("::ffff:10.0.0.1").Ipv4Mapped);
        Assert.Null(Address("2001:db8::1").Ipv4Mapped);
        Assert.Null(Address("10.0.0.1").Ipv4Mapped);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("128.0.0.1", false)]
    [InlineData("::1", true)]
    [InlineData("::2", false)]
    public void Loopback_is_the_127_block_or_the_one_ipv6_address(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsLoopback);

    [Theory]
    [InlineData("169.254.10.1", true)]
    [InlineData("169.255.0.1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("fec0::1", false)]
    public void Link_local_is_the_169_254_block_or_fe80_slash_10(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsLinkLocal);

    [Theory]
    [InlineData("224.0.0.1", true)]
    [InlineData("239.255.255.255", true)]
    [InlineData("223.255.255.255", false)]
    [InlineData("ff02::1", true)]
    [InlineData("fe02::1", false)]
    public void Multicast_is_the_224_block_or_ff00_slash_8(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsMulticast);

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("0.0.0.1", false)]
    [InlineData("::1", false)]
    public void Unspecified_is_the_all_zero_address(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsUnspecified);

    [Theory]
    [InlineData("240.0.0.1", true)]
    [InlineData("239.0.0.1", false)]
    [InlineData("::1", true)]
    [InlineData("100::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2a00:1450::1", false)]
    [InlineData("fe00::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    public void Reserved_addresses_are_the_ones_the_registry_has_not_handed_out(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsReserved);

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.5.5", true)]
    [InlineData("203.0.113.9", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2a00:1450:4001::1", false)]
    public void Private_addresses_are_the_special_purpose_ranges(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsPrivate);

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("2a00:1450:4001::1", true)]
    [InlineData("fd12:3456::1", false)]
    public void Global_addresses_are_neither_private_nor_in_the_shared_carrier_block(string text, bool expected) =>
        Assert.Equal(expected, Address(text).IsGlobal);

    [Fact]
    public void An_address_is_inside_a_network_of_its_own_family_only()
    {
        var network = Network("10.0.0.0/8");

        Assert.True(Address("10.200.1.1").In(network));
        Assert.False(Address("11.0.0.1").In(network));
        Assert.False(Address("::ffff:10.0.0.1").In(network));
        Assert.Throws<ArgumentNullException>(() => Address("10.0.0.1").In(null!));
        Assert.Throws<ArgumentNullException>(() => network.Contains(null!));
    }

    [Fact]
    public void A_network_of_every_address_contains_every_address_of_its_family()
    {
        Assert.True(Address("203.0.113.9").In(Network("0.0.0.0/0")));
        Assert.True(Address("2001:db8::9").In(Network("::/0")));
    }

    [Fact]
    public void A_network_built_from_parts_must_have_an_address_of_the_right_family()
    {
        Assert.Equal(Network("10.0.0.0/8"), NetRange.ParseV4("10.0.0.0", 8));
        Assert.Equal(Network("fe80::/10"), NetRange.ParseV6("fe80::", 10));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV4("fe80::", 10));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV4("nonsense", 8));
        Assert.Throws<ArgumentException>(() => NetRange.ParseV6("10.0.0.0", 8));
    }

    [Theory]
    [InlineData("10.0.0.7", "10.0.0.7", 32)]
    [InlineData("::1", "::1", 128)]
    [InlineData("10.0.0.0/8", "10.0.0.0", 8)]
    [InlineData("10.0.0.0/0", "0.0.0.0", 0)]
    [InlineData("10.0.0.5/24", "10.0.0.0", 24)]
    [InlineData("10.20.30.40/255.255.0.0", "10.20.0.0", 16)]
    [InlineData("10.20.30.40/255.255.255.255", "10.20.30.40", 32)]
    [InlineData("10.20.30.40/0.0.0.0", "0.0.0.0", 0)]
    [InlineData("2001:db8:1234::5/48", "2001:db8:1234::", 48)]
    [InlineData("2001:db8::/32", "2001:db8::", 32)]
    public void A_network_is_an_address_with_a_prefix_a_mask_or_nothing_and_host_bits_are_masked_off(string text, string address, int prefix)
    {
        var network = Network(text);

        Assert.Equal(Address(address).Value, network.NetworkAddress);
        Assert.Equal(prefix, network.PrefixLength);
        Assert.Equal(Address(address).IsV6, network.IsV6);
    }

    [Fact]
    public void A_strict_network_refuses_host_bits_and_accepts_none()
    {
        Assert.False(NetRange.TryParse("10.0.0.5/24", strict: true, out _));
        Assert.False(NetRange.TryParse("10.0.0.5/255.255.255.0", strict: true, out _));
        Assert.True(NetRange.TryParse("10.0.0.0/24", strict: true, out var network));
        Assert.Equal(24, network.PrefixLength);
        Assert.True(NetRange.TryParse("10.0.0.5", strict: true, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("nonsense/8")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/08")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/99999999999999999999")]
    [InlineData("10.0.0.0/255.0.255.0")]
    [InlineData("10.0.0.0/255.255.255")]
    [InlineData("::1/129")]
    [InlineData("::1/255.0.0.0")]
    [InlineData("fe80::1%eth0")]
    [InlineData("fe80::1%eth0/64")]
    public void Text_that_is_not_a_network_is_not_read_and_leaves_the_single_zero_host(string? text)
    {
        Assert.False(NetRange.TryParse(text, strict: false, out var network));
        Assert.Equal(new NetRange(false, BigInteger.Zero, 32), network);
    }

    [Theory]
    [InlineData("10.1.2.0/24", "0A010200")]
    [InlineData("0.0.0.0/0", "00000000")]
    [InlineData("2001:db8::/32", "20010DB8000000000000000000000000")]
    [InlineData("::/0", "00000000000000000000000000000000")]
    [InlineData("::1/128", "00000000000000000000000000000001")]
    public void The_network_address_is_given_in_network_byte_order_at_the_width_of_its_family(string text, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(Network(text).AddressBytes()));
}
