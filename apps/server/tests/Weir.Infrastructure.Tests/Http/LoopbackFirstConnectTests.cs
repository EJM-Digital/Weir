using System.Net;
using System.Net.Sockets;
using System.Text;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// A program that listens on IPv4 only must be reachable as <c>localhost</c> without Windows first retrying a refused
/// connect to ::1 for about two seconds. The listener here is bound to 127.0.0.1 alone. How long a connect takes depends on
/// the machine, so what is checked is the order: where the same port is also open on ::1, an answer from the IPv4 listener
/// proves ::1 was not tried first.
/// </summary>
public sealed class LoopbackFirstConnectTests : IDisposable
{
    private static readonly byte[] OkResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
    private static readonly byte[] WrongListenerResponse = Encoding.ASCII.GetBytes("HTTP/1.1 418 Wrong listener\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly TcpListener _listener;
    private readonly TcpListener? _ipv6Listener;
    private readonly CancellationTokenSource _stop = new();

    public LoopbackFirstConnectTests()
    {
        (_listener, _ipv6Listener) = StartListeners();
        if (_ipv6Listener is not null)
        {
            _ = ServeAsync(_ipv6Listener, WrongListenerResponse);
        }
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _ipv6Listener?.Stop();
        _stop.Dispose();
    }

    [Fact]
    public async Task The_manager_handler_reaches_an_ipv4_only_listener_as_localhost_by_way_of_ipv4()
    {
        using var factory = new SocketsManagerHttpHandlerFactory();

        await AssertReachedOverIpv4Async(factory.Handler(followRedirects: false, ManagerAddressPolicy.Local));
    }

    [Fact]
    public async Task The_redirect_following_manager_handler_reaches_an_ipv4_only_listener_as_localhost_by_way_of_ipv4()
    {
        using var factory = new SocketsManagerHttpHandlerFactory();

        await AssertReachedOverIpv4Async(factory.Handler(followRedirects: true, ManagerAddressPolicy.Local));
    }

    [Fact]
    public async Task A_plain_handler_using_the_shared_callback_reaches_an_ipv4_only_listener_as_localhost_by_way_of_ipv4()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = LoopbackFirstConnect.ConnectAsync };

        await AssertReachedOverIpv4Async(handler);
    }

    [Fact]
    public async Task The_host_name_is_matched_without_regard_to_case()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = LoopbackFirstConnect.ConnectAsync };
        using var client = new HttpClient(handler, disposeHandler: false);
        _ = ServeAsync();

        using var response = await client.GetAsync(new Uri($"http://LocalHost:{Port}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_address_literal_connects_as_the_handler_would_by_default()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = LoopbackFirstConnect.ConnectAsync };
        using var client = new HttpClient(handler, disposeHandler: false);
        _ = ServeAsync();

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{Port}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_candidate_falls_through_to_the_next()
    {
        _ = ServeAsync();
        IPAddress[] candidates = Socket.OSSupportsIPv6 ? [IPAddress.IPv6Loopback, IPAddress.Loopback] : [IPAddress.Loopback];

        await using var stream = await LoopbackFirstConnect.ConnectAsync(candidates, Port, CancellationToken.None);

        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task When_no_candidate_answers_the_failure_is_thrown()
    {
        await Assert.ThrowsAsync<SocketException>(async () => await LoopbackFirstConnect.ConnectAsync([IPAddress.Loopback], FreePort(), CancellationToken.None));
    }

    [Fact]
    public async Task An_address_literal_nobody_listens_on_is_a_failed_request_not_a_leaked_socket()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = LoopbackFirstConnect.ConnectAsync };
        using var client = new HttpClient(handler, disposeHandler: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{FreePort()}/")));
    }

    [Fact]
    public async Task Connecting_to_no_address_at_all_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await LoopbackFirstConnect.ConnectAsync([], Port, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_connect_throws_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LoopbackFirstConnect.ConnectAsync([IPAddress.Loopback], Port, cancelled.Token));
    }

    [Fact]
    public void Localhost_is_tried_on_ipv4_before_ipv6()
    {
        var addresses = LoopbackFirstConnect.LoopbackAddresses();

        Assert.Equal(IPAddress.Loopback, addresses[0]);
        Assert.All(addresses.Skip(1), address => Assert.Equal(IPAddress.IPv6Loopback, address));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("localhost.", true)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("127.0.0.1", false)]
    public void Only_the_name_localhost_is_special(string host, bool expected) =>
        Assert.Equal(expected, LoopbackFirstConnect.IsLocalhost(host));

    private async Task AssertReachedOverIpv4Async(HttpMessageHandler handler)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        _ = ServeAsync();

        using var response = await client.GetAsync(new Uri($"http://localhost:{Port}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task ServeAsync() => ServeAsync(_listener, OkResponse);

    private async Task ServeAsync(TcpListener listener, byte[] response)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                _ = await stream.ReadAsync(new byte[1024], _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException or IOException)
        {
        }
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    /// <summary>An IPv4 listener and, where the machine has IPv6, one on the same port at ::1 (a few ports are tried, in case another program holds that one).</summary>
    private static (TcpListener Ipv4, TcpListener? Ipv6) StartListeners()
    {
        for (var attempt = 0; attempt < 10 && Socket.OSSupportsIPv6; attempt++)
        {
            var ipv4 = StartListener();
            var ipv6 = new TcpListener(IPAddress.IPv6Loopback, ((IPEndPoint)ipv4.LocalEndpoint).Port);
            try
            {
                ipv6.Start();
                return (ipv4, ipv6);
            }
            catch (SocketException)
            {
                ipv4.Stop();
            }
        }

        return (StartListener(), null);
    }

    private static int FreePort()
    {
        var probe = StartListener();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
