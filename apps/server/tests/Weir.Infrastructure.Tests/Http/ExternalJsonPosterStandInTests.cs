using System.Net;
using System.Net.Sockets;
using System.Text;
using Weir.Core.Notifications;
using Weir.Infrastructure.Http;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// The poster as an alert uses it, against a real listener on this machine: the request that arrives, and the address rules that still
/// apply when a test sends every alert to that listener (<c>WEIR_NOTIFICATION_STAND_IN_PORT</c>).
/// </summary>
public sealed class ExternalJsonPosterStandInTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string CrLf = "\r\n";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _port;
    private int _answer = 204;
    private string? _location;
    private Received? _request;
    private string _body = string.Empty;

    public ExternalJsonPosterStandInTests()
    {
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private sealed record Received(string Method, string RawUrl, string Host, string ContentType, string UserAgent);

    public void Dispose() => _listener.Stop();

    private async Task<(int Status, Task Served)> PostAsync(string url, int? standIn)
    {
        var served = ServeOneAsync();
        var status = await new ExternalJsonPoster(standIn).PostJsonAsync(
            url, Encoding.UTF8.GetBytes("{\"event\": \"x\"}"), new Dictionary<string, string> { ["User-Agent"] = "Weir/1.0" }, Timeout, CancellationToken.None);
        return (status, served);
    }

    /// <summary>Answers one request over a plain socket, so what arrives is exactly what the poster wrote, whatever the host name says.</summary>
    private async Task ServeOneAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var requestLine = (await reader.ReadLineAsync())!.Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        var body = new char[int.Parse(headers.GetValueOrDefault("Content-Length", "0"), System.Globalization.CultureInfo.InvariantCulture)];
        await reader.ReadBlockAsync(body);
        _body = new string(body);
        _request = new Received(requestLine[0], requestLine[1], headers.GetValueOrDefault("Host", string.Empty), headers.GetValueOrDefault("Content-Type", string.Empty), headers.GetValueOrDefault("User-Agent", string.Empty));

        var location = _location is null ? string.Empty : $"Location: {_location}" + CrLf;
        var answer = $"HTTP/1.1 {_answer} Answer" + CrLf + "Content-Length: 0" + CrLf + "Connection: close" + CrLf + location + CrLf;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
    }

    [Fact]
    public async Task An_alert_to_a_public_looking_name_arrives_as_a_json_post_with_the_documented_headers()
    {
        var (status, served) = await PostAsync("http://hooks.example.com/alerts/7?token=abc", _port);
        await served.WaitAsync(Timeout);

        Assert.Equal(204, status);
        Assert.Equal("POST", _request!.Method);
        Assert.Equal("/alerts/7?token=abc", _request.RawUrl);
        Assert.Equal("hooks.example.com", _request.Host);
        Assert.StartsWith("application/json", _request.ContentType, StringComparison.Ordinal);
        Assert.Equal("Weir/1.0", _request.UserAgent);
        Assert.Equal("{\"event\": \"x\"}", _body);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_destinations_error_answer_comes_back_as_its_status(int answer)
    {
        _answer = answer;

        var (status, served) = await PostAsync("http://hooks.example.com/alerts", _port);
        await served.WaitAsync(Timeout);

        Assert.Equal(answer, status);
    }

    [Fact]
    public async Task A_redirect_is_refused_and_not_followed()
    {
        _answer = 302;
        _location = "http://hooks.example.com/elsewhere";

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(async () => await (await PostAsync("http://hooks.example.com/alerts", _port)).Served);
        Assert.Equal(ExternalUrlPolicy.RedirectRefused, refused.Message);
    }

    [Theory]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.20/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://169.254.169.254/latest")]
    public async Task An_address_written_as_numbers_is_still_refused_when_it_is_not_public(string url)
    {
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => new ExternalJsonPoster(_port).PostJsonAsync(
            url, [], new Dictionary<string, string>(), Timeout, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.NonPublicAddress, refused.Message);
    }

    [Fact]
    public async Task A_name_that_is_this_machine_is_still_refused()
    {
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => new ExternalJsonPoster(_port).PostJsonAsync(
            "http://localhost/hook", [], new Dictionary<string, string>(), Timeout, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.NonPublicAddress, refused.Message);
    }

    [Fact]
    public async Task Embedded_credentials_are_refused_with_a_stand_in_too()
    {
        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => new ExternalJsonPoster(_port).PostJsonAsync(
            "http://user:secret@hooks.example.com/hook", [], new Dictionary<string, string>(), Timeout, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.EmbeddedCredentials, refused.Message);
    }

    [Fact]
    public async Task Nothing_listening_where_the_stand_in_is_reads_as_could_not_connect()
    {
        using var held = HoldPortNothingListensOn();
        var port = ((IPEndPoint)held[0].LocalEndPoint!).Port;

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => new ExternalJsonPoster(port).PostJsonAsync(
            "http://hooks.example.com/hook", [], new Dictionary<string, string>(), Timeout, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.CouldNotConnect, refused.Message);
    }

    /// <summary>
    /// A port on every loopback address that is bound and never listened on: a connect to it is refused, and no other test running at the same
    /// time can be given the port, which a listener that was stopped would let it.
    /// </summary>
    private static HeldPort HoldPortNothingListensOn()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var held = new HeldPort();
            try
            {
                var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                held.Add(first);
                first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                if (Socket.OSSupportsIPv6)
                {
                    var second = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                    held.Add(second);
                    second.Bind(new IPEndPoint(IPAddress.IPv6Loopback, ((IPEndPoint)first.LocalEndPoint!).Port));
                }

                return held;
            }
            catch (SocketException)
            {
                held.Dispose();
            }
        }

        throw new InvalidOperationException("No loopback port could be held on both address families.");
    }

    private sealed class HeldPort : List<Socket>, IDisposable
    {
        public void Dispose()
        {
            foreach (var socket in this)
            {
                socket.Dispose();
            }
        }
    }
}
