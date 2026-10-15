using System.Net.Sockets;

namespace Weir.Host;

/// <summary>
/// A server whose port is held by another program. Kestrel reports it as an <see cref="IOException"/> from the moment the server starts
/// listening, with the socket's own refusal (<see cref="SocketError.AddressAlreadyInUse"/>) somewhere inside it.
/// </summary>
internal static class PortInUse
{
    /// <summary>Whether <paramref name="exception"/> is the listener failing to bind because another program holds the port.</summary>
    internal static bool Caused(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not IOException)
        {
            return false;
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What the person is told: which port, and what to do about it.</summary>
    internal static string Sentence(int port) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"port {port} is already in use by another program. Close that program, or run Weir on a different port.");
}
