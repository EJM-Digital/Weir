using System.Net;
using System.Net.Sockets;
using System.Text;
using Weir.Core.Notifications;
using Weir.Infrastructure.Http;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// An alert address that is not a usable address, and a destination that takes the connection and then fails it, each told in its own plain
/// words. The destination is a real listener on this machine that the poster is pointed at (<c>WEIR_NOTIFICATION_STAND_IN_PORT</c>).
/// </summary>
public sealed class ExternalJsonPosterRefusalsTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _port;

    public ExternalJsonPosterRefusalsTests()
    {
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose() => _listener.Stop();

    private Task<int> PostAsync(string url, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new ExternalJsonPoster(_port).PostJsonAsync(url, [], headers ?? new Dictionary<string, string>(), timeout ?? Ceiling, CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hooks.example.com/alert")]
    [InlineData("ftp://hooks.example.com/alert")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http:///alert")]
    public async Task An_address_that_is_not_an_http_or_https_address_is_refused(string url)
    {
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => PostAsync(url));

        Assert.Equal(ExternalUrlPolicy.InvalidDestination, refused.Message);
    }

    [Theory]
    [InlineData("http://hooks.example.com:notaport/alert")]
    [InlineData("http://hooks.example.com:65536/alert")]
    public async Task A_port_that_is_not_a_port_is_refused(string url)
    {
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => PostAsync(url));

        Assert.Equal(ExternalUrlPolicy.InvalidPort, refused.Message);
    }

    [Fact]
    public async Task A_name_that_cannot_be_looked_up_is_refused_as_one_Weir_could_not_resolve()
    {
        // The real resolver, which has no stand-in: ".invalid" is reserved so that it never names anything.
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(
            () => new ExternalJsonPoster().PostJsonAsync("http://hooks.invalid/alert", [], new Dictionary<string, string>(), Ceiling, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.CouldNotResolve, refused.Message);
    }

    [Fact]
    public async Task A_destination_that_hangs_up_without_answering_reads_as_could_not_deliver()
    {
        var accepted = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
        });

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => PostAsync("http://hooks.example.com/alert"));
        await accepted.WaitAsync(Ceiling);

        Assert.Equal(ExternalUrlPolicy.CouldNotDeliver, refused.Message);
    }

    [Fact]
    public async Task A_destination_that_never_answers_reads_as_could_not_deliver_when_the_time_is_up()
    {
        using var release = new CancellationTokenSource();
        var accepted = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await Task.Delay(Ceiling, release.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        });

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => PostAsync("http://hooks.example.com/alert", TimeSpan.FromMilliseconds(300)));
        await release.CancelAsync();
        await accepted.WaitAsync(Ceiling);

        Assert.Equal(ExternalUrlPolicy.CouldNotDeliver, refused.Message);
    }

    [Fact]
    public async Task A_secure_address_whose_destination_does_not_speak_TLS_reads_as_could_not_secure()
    {
        var accepted = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
        });

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => PostAsync("https://hooks.example.com/alert"));
        await accepted.WaitAsync(Ceiling);

        Assert.Equal(ExternalUrlPolicy.CouldNotSecure, refused.Message);
    }

    [Fact]
    public async Task A_header_that_belongs_to_the_body_is_sent_with_the_body()
    {
        string? received = null;
        var served = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var headerLines = new List<string>();
            for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
            {
                headerLines.Add(line);
            }

            received = headerLines.Single(line => line.StartsWith("Content-Language:", StringComparison.OrdinalIgnoreCase));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
        });

        var status = await PostAsync("http://hooks.example.com/alert", headers: new Dictionary<string, string> { ["Content-Language"] = "en" });
        await served.WaitAsync(Ceiling);

        Assert.Equal(204, status);
        Assert.Equal("Content-Language: en", received);
    }

    [Fact]
    public async Task An_address_with_a_numeric_IPv6_host_is_sent_to_the_stand_in_like_any_other_public_one()
    {
        var served = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });

        var status = await PostAsync("http://[2001:4860:4860::8888]/alert");
        await served.WaitAsync(Ceiling);

        Assert.Equal(202, status);
    }

    [Fact]
    public void The_endpoint_exception_carries_a_message_and_the_error_that_caused_it()
    {
        var cause = new IOException("the cause");

        Assert.Equal("a message", new ExternalEndpointException("a message").Message);
        Assert.Same(cause, new ExternalEndpointException("a message", cause).InnerException);
        Assert.NotNull(new ExternalEndpointException().Message);
    }
}
