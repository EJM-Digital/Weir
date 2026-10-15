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

    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _answer = 204;
    private string? _location;
    private Received? _request;
    private string _body = string.Empty;

    public ExternalJsonPosterStandInTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
    }

    private sealed record Received(string Method, string RawUrl, string Host, string ContentType, string UserAgent);

    public void Dispose() => _listener.Abort();

    private async Task<(int Status, Task Served)> PostAsync(string url, int? standIn)
    {
        var served = ServeOneAsync();
        var status = await new ExternalJsonPoster(standIn).PostJsonAsync(
            url, Encoding.UTF8.GetBytes("{\"event\": \"x\"}"), new Dictionary<string, string> { ["User-Agent"] = "Weir/1.0" }, Timeout, CancellationToken.None);
        return (status, served);
    }

    private async Task ServeOneAsync()
    {
        var context = await _listener.GetContextAsync();
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            _body = await reader.ReadToEndAsync();
        }

        var request = context.Request;
        _request = new Received(request.HttpMethod, request.RawUrl ?? string.Empty, request.UserHostName, request.ContentType ?? string.Empty, request.UserAgent ?? string.Empty);

        context.Response.StatusCode = _answer;
        if (_location is not null)
        {
            context.Response.AddHeader("Location", _location);
        }

        context.Response.Close();
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
        _listener.Stop();

        var refused = await Assert.ThrowsAsync<ExternalEndpointException>(() => new ExternalJsonPoster(_port).PostJsonAsync(
            "http://hooks.example.com/hook", [], new Dictionary<string, string>(), Timeout, CancellationToken.None));

        Assert.Equal(ExternalUrlPolicy.CouldNotConnect, refused.Message);
    }
}
