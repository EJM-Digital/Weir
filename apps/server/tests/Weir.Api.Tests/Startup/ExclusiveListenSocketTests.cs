using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Weir.Host;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests.Startup;

/// <summary>On Windows the server's listening sockets are bound for exclusive use of their address; elsewhere nothing changes.</summary>
public sealed class ExclusiveListenSocketTests
{
    [Fact]
    public void Kestrels_socket_transport_binds_exclusively_on_windows_and_unchanged_elsewhere()
    {
        var options = new SocketTransportOptions();

        ExclusiveListenSocket.Configure(options);

        using var socket = options.CreateBoundListenSocket(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.Equal(OperatingSystem.IsWindows(), socket.ExclusiveAddressUse);
    }

    [Fact]
    public void An_endpoint_that_is_not_an_address_and_port_is_bound_the_way_kestrel_would()
    {
        var path = Path.Join(Path.GetTempPath(), "weir-exclusive-" + Guid.NewGuid().ToString("N") + ".sock");
        try
        {
            using var socket = ExclusiveListenSocket.Create(new UnixDomainSocketEndPoint(path));

            Assert.Equal(AddressFamily.Unix, socket.AddressFamily);
            Assert.True(socket.IsBound);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [WindowsFact("Exclusive use of an address is a Windows socket option.")]
    public void A_port_already_bound_exclusively_cannot_be_bound_again_and_the_failed_socket_is_released()
    {
        using var first = ExclusiveListenSocket.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var taken = (IPEndPoint)first.LocalEndPoint!;

        var refused = Assert.Throws<SocketException>(() => ExclusiveListenSocket.Create(new IPEndPoint(IPAddress.Loopback, taken.Port)));

        Assert.Equal(SocketError.AddressAlreadyInUse, refused.SocketErrorCode);
        Assert.True(first.ExclusiveAddressUse);
    }

    [WindowsFact("Exclusive use of an address is a Windows socket option.")]
    public void The_ipv6_wildcard_also_takes_ipv4()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var socket = ExclusiveListenSocket.Create(new IPEndPoint(IPAddress.IPv6Any, 0));

        Assert.True(socket.DualMode);
    }
}
