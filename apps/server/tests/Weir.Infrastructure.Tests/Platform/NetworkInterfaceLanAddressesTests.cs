using System.Net;
using System.Net.Sockets;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Platform;

public sealed class NetworkInterfaceLanAddressesTests
{
    [Fact]
    public void The_addresses_this_pc_reports_are_IPv4_and_never_self_assigned()
    {
        var addresses = new NetworkInterfaceLanAddresses().Read();

        foreach (var text in addresses)
        {
            Assert.Equal(AddressFamily.InterNetwork, IPAddress.Parse(text).AddressFamily);
            Assert.False(text.StartsWith("169.254.", StringComparison.Ordinal));
        }

        Assert.Equal(addresses.Count, addresses.Distinct().Count());
    }
}
