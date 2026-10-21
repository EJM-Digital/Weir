using System.Net;
using Microsoft.AspNetCore.Http;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Http;

/// <summary>The Host allow-list middleware, wired into the real pipeline (see AllowedHostPolicyTests for the rule itself).</summary>
public sealed class HostAllowListMiddlewareTests
{
    private static Task<HttpContext> RequestAsync(WeirTestServer server, string host, string peer = "203.0.113.9") =>
        server.TestServer.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "GET";
            context.Request.Path = "/health";
            context.Request.Headers.Host = host;
        });

    private static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task An_ip_literal_host_reaches_health_from_a_non_loopback_peer()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret)]);

        var context = await RequestAsync(server, "192.0.2.10:9347");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task An_unrecognised_host_is_refused_with_a_plain_text_400()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret)]);

        var context = await RequestAsync(server, "evil.example");
        var body = await BodyAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.StartsWith("text/plain", context.Response.ContentType);
        Assert.Contains("evil.example", body, StringComparison.Ordinal);
        Assert.Contains("WEIR_ALLOWED_HOSTS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_domain_in_weir_allowed_hosts_reaches_health()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_ALLOWED_HOSTS", "weir.example,*.internal-weir.example")]);

        var direct = await RequestAsync(server, "weir.example");
        var subdomain = await RequestAsync(server, "nas.internal-weir.example");
        var other = await RequestAsync(server, "other.example");

        Assert.Equal(StatusCodes.Status200OK, direct.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, subdomain.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, other.Response.StatusCode);
    }

    [Fact]
    public async Task A_trusted_browser_origins_host_reaches_health()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_CORS_ORIGINS", "https://weir.example")]);

        var context = await RequestAsync(server, "weir.example");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_request_from_a_trusted_proxy_skips_the_host_check()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/24")]);

        var fromProxy = await RequestAsync(server, "evil.example", peer: "10.0.0.5");
        var direct = await RequestAsync(server, "evil.example", peer: "203.0.113.9");

        Assert.Equal(StatusCodes.Status200OK, fromProxy.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, direct.Response.StatusCode);
    }

    [Fact]
    public async Task A_refused_host_is_warned_about_once_however_often_it_asks()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret)]);

        var first = await RequestAsync(server, "evil.example");
        var second = await RequestAsync(server, "evil.example");

        Assert.Equal([StatusCodes.Status400BadRequest, StatusCodes.Status400BadRequest], [first.Response.StatusCode, second.Response.StatusCode]);
        using var log = new StreamReader(new FileStream(Path.Join(server.Home, "logs", "weir.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var warnings = System.Text.RegularExpressions.Regex.Count(await log.ReadToEndAsync(), "Refused a request with Host 'evil.example'");
        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task Health_answers_for_a_loopback_healthcheck_with_no_configuration()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret)]);

        var context = await RequestAsync(server, "127.0.0.1:9347", peer: "127.0.0.1");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }
}
