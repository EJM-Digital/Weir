using System.Net;
using System.Net.Sockets;

namespace Weir.Infrastructure.Http;

/// <summary>
/// How every outbound connection is made. A host named <c>localhost</c> connects to 127.0.0.1 first and ::1 second:
/// Windows tries ::1 first, and when the program on the other end listens on IPv4 only it retries the refused
/// connect for about two seconds before falling back. Every other host connects the way
/// <see cref="SocketsHttpHandler"/> does by default. Stateless: nothing here is per-connection or per-request.
/// </summary>
public static class LoopbackFirstConnect
{
    /// <summary>A <see cref="SocketsHttpHandler.ConnectCallback"/>; the connection's own target host decides the order.</summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        return IsLocalhost(endpoint.Host)
            ? ConnectAsync(LoopbackAddresses(), endpoint.Port, cancellationToken)
            : ConnectHostAsync(endpoint, cancellationToken);
    }

    /// <summary>Whether <paramref name="host"/> is the name <c>localhost</c>, in any case and with or without a trailing dot.</summary>
    public static bool IsLocalhost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loopback addresses in the order <c>localhost</c> is tried: IPv4, then IPv6 where the OS has it.</summary>
    public static IReadOnlyList<IPAddress> LoopbackAddresses() => LoopbackAddresses(Socket.OSSupportsIPv6);

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): the same order for an OS a test says has, or lacks, IPv6.</summary>
    internal static IReadOnlyList<IPAddress> LoopbackAddresses(bool osSupportsIPv6) =>
        osSupportsIPv6 ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : [IPAddress.Loopback];

    /// <summary>
    /// Connects to the first of <paramref name="addresses"/> that answers, in order, and returns a stream that owns the
    /// socket. When none answers, the last failure is rethrown.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            throw new ArgumentException("At least one address is required.", nameof(addresses));
        }

        for (var index = 0; ; index++)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses[index], port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (exception is OperationCanceledException || index == addresses.Count - 1)
                {
                    throw;
                }
            }
        }
    }

    private static async ValueTask<Stream> ConnectHostAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
    }
}
