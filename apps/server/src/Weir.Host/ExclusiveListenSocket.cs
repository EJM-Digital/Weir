using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Weir.Host;

/// <summary>
/// On Windows the server binds its listening sockets for exclusive use of their address (<c>SO_EXCLUSIVEADDRUSE</c>, which Windows
/// recommends for servers). Without it Windows lets a bind to one address sit beside a bind to every address on the same port, so another
/// program holding <c>127.0.0.1</c> alone could share Weir's port, and a program started after Weir could take over its port for callers on
/// this PC. With it, a port anyone already holds on an overlapping address refuses to bind, which ends the start as a port in use
/// (<see cref="PortInUse"/>), and nothing can bind beside the server afterwards. Elsewhere the platform's own rules already do this and
/// nothing changes.
/// </summary>
internal static class ExclusiveListenSocket
{
    /// <summary>Makes Kestrel's socket transport bind exclusively, on Windows.</summary>
    internal static void Configure(SocketTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows())
        {
            options.CreateBoundListenSocket = Create;
        }
    }

    /// <summary>Binds a listening socket for <paramref name="endpoint"/> that no other socket can share.</summary>
    internal static Socket Create(EndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint is not IPEndPoint address)
        {
            return SocketTransportOptions.CreateDefaultBoundListenSocket(endpoint);
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Kestrel expects the IPv6 wildcard to take IPv4 as well.
            if (address.Address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            socket.ExclusiveAddressUse = true;
            socket.Bind(address);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
