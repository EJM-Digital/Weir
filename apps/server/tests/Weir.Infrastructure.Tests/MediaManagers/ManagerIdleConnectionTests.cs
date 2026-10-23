using System.Net;
using System.Net.Sockets;
using System.Text;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A manager, or a proxy in front of it, closes a connection that has sat idle. A call that goes out on a connection in the instant it
/// is closed fails with a reset ("Weir could not reach" a manager that is running), and the transport cannot send it again because the
/// request may already have been acted on. So Weir stops reusing a connection well before the shortest idle limit it meets: .NET's
/// HttpListener, which the contract suite's fake managers run on, closes an idle connection after 15 seconds.
/// </summary>
public sealed class ManagerIdleConnectionTests : IDisposable
{
    private static readonly byte[] Answer = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\n\r\n{\"status\":\"ok\"}");

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource _firstConnectionDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connections;

    public ManagerIdleConnectionTests()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public void Dispose() => _listener.Stop();

    private string BaseUrl => $"http://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    [Fact]
    public void A_connection_is_kept_for_less_time_than_the_shortest_idle_limit_measured()
    {
        Assert.True(SocketsManagerHttpHandlerFactory.DefaultIdleConnectionTimeout < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_connection_is_reused_while_it_is_fresh()
    {
        using var handlers = new SocketsManagerHttpHandlerFactory(idleConnectionTimeout: TimeSpan.FromMinutes(1));
        var client = new MediaManagerHttpClient(BaseUrl, "key", handlers);

        await client.GetJsonAsync("/first");
        await client.GetJsonAsync("/second");

        Assert.Equal(1, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task A_connection_that_has_sat_idle_longer_than_the_limit_is_replaced_rather_than_reused()
    {
        var limit = TimeSpan.FromMilliseconds(200);
        using var handlers = new SocketsManagerHttpHandlerFactory(idleConnectionTimeout: limit);
        var client = new MediaManagerHttpClient(BaseUrl, "key", handlers);
        await client.GetJsonAsync("/first");

        // The pool drops an idle connection on its own schedule; the server sees that as the client closing the connection.
        await _firstConnectionDropped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await client.GetJsonAsync("/second");

        Assert.Equal(2, Volatile.Read(ref _connections));
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                    await stream.WriteAsync(Answer).ConfigureAwait(false);
                }

                _firstConnectionDropped.TrySetResult();
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
