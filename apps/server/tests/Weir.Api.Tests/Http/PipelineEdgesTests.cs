using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Api.Http;
using Weir.Api.Web;
using Weir.Core.Configuration;

namespace Weir.Api.Tests.Http;

/// <summary>The middleware and request checks at their edges, run on requests that carry less than a real one: no path, no peer, no matching route.</summary>
public sealed class PipelineEdgesTests
{
    private static readonly RequestDelegate Reached = context =>
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    };

    private static DefaultHttpContext Request(string method = "GET", string? host = null, IPAddress? peer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (host is not null)
        {
            context.Request.Headers.Host = host;
        }

        context.Connection.RemoteIpAddress = peer;
        return context;
    }

    private static async Task<WeirOptions> OptionsAsync(params string[] trustedProxies)
    {
        await using var server = await WeirTestServer.StartAsync();
        return server.Services.GetRequiredService<WeirOptions>() with { TrustedProxyIps = trustedProxies };
    }

    private static async Task<bool> PassedHostCheckAsync(WeirOptions options, DefaultHttpContext context)
    {
        var middleware = new HostAllowListMiddleware(Reached, options, NullLoggerFactory.Instance, TimeProvider.System);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode == StatusCodes.Status204NoContent;
    }

    [Fact]
    public async Task A_request_from_a_trusted_proxy_skips_the_host_check_whether_or_not_the_peer_was_marked_earlier()
    {
        var options = await OptionsAsync("10.0.0.0/8");
        var marked = Request(host: "evil.example.com");
        marked.Items[ResponseMarkers.ConnectionPeer] = "10.1.2.3";

        Assert.True(await PassedHostCheckAsync(options, marked));
        Assert.True(await PassedHostCheckAsync(options, Request(host: "evil.example.com", peer: IPAddress.Parse("10.1.2.3"))));
        Assert.True(await PassedHostCheckAsync(options, Request(host: "evil.example.com", peer: IPAddress.Parse("::ffff:10.1.2.3"))));
    }

    [Fact]
    public async Task A_request_from_anywhere_else_is_held_to_the_host_check()
    {
        var options = await OptionsAsync("10.0.0.0/8");
        var marked = Request(host: "evil.example.com");
        marked.Items[ResponseMarkers.ConnectionPeer] = "192.0.2.9";

        Assert.False(await PassedHostCheckAsync(options, marked));
        Assert.False(await PassedHostCheckAsync(options, Request(host: "evil.example.com")));
        Assert.False(await PassedHostCheckAsync(options, Request(host: "evil.example.com", peer: IPAddress.Parse("192.0.2.9"))));
    }

    [Fact]
    public async Task A_browser_change_with_no_path_is_not_taken_for_an_api_call()
    {
        var context = Request("POST");
        context.Request.Headers.Origin = "http://weir.local";

        await new XRequestedWithMiddleware(Reached).InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public void A_response_with_no_path_still_gets_the_baseline_headers_and_no_cache_control()
    {
        var context = Request();

        SecurityHeadersMiddleware.Apply(context, enableHsts: false);

        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound, null, null, false)]
    [InlineData(StatusCodes.Status405MethodNotAllowed, null, null, true)]
    [InlineData(StatusCodes.Status405MethodNotAllowed, 0L, null, true)]
    [InlineData(StatusCodes.Status405MethodNotAllowed, 5L, null, false)]
    [InlineData(StatusCodes.Status405MethodNotAllowed, null, "text/plain", false)]
    public async Task Only_a_bare_405_is_given_the_standard_body(int status, long? length, string? contentType, bool filled)
    {
        var routes = new RouteTable();
        routes.Add("/api/v1/thing", ["GET", "PUT"], "/api/v1/thing");
        var context = Request("DELETE");
        context.Request.Path = "/api/v1/thing";
        context.Response.Body = new MemoryStream();

        await new MethodNotAllowedBodyMiddleware(
            inner =>
            {
                inner.Response.StatusCode = status;
                inner.Response.ContentLength = length;
                inner.Response.ContentType = contentType;
                return Task.CompletedTask;
            },
            routes).InvokeAsync(context);

        Assert.Equal(filled, context.Response.Headers.Allow.ToString() == "GET, PUT");
    }

    [Fact]
    public async Task A_405_for_a_path_no_route_names_has_a_body_but_no_allow_header()
    {
        var context = Request("DELETE");
        context.Request.Path = "/nowhere";
        context.Response.Body = new MemoryStream();

        await new MethodNotAllowedBodyMiddleware(
            inner =>
            {
                inner.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return Task.CompletedTask;
            },
            new RouteTable()).InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Allow"));
        Assert.True(context.Response.Body.Length > 0);
    }

    [Fact]
    public void A_request_is_named_by_its_endpoints_label_then_its_matching_route_then_its_path()
    {
        var routes = new RouteTable();
        routes.Add("/api/v1/thing", ["GET"], "/api/v1/thing");
        var labelled = Request();
        labelled.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new RouteLabel("/labelled")), "labelled"));
        var unlabelled = Request();
        unlabelled.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "unlabelled"));
        var matched = Request();
        matched.Request.Path = "/api/v1/thing";
        var bare = Request();
        bare.Request.Path = "/somewhere";

        Assert.Equal("/labelled", RequestRoute.LabelFor(labelled, routes));
        Assert.Equal(string.Empty, RequestRoute.LabelFor(unlabelled, routes));
        Assert.Equal("/api/v1/thing", RequestRoute.LabelFor(matched, routes));
        Assert.Equal("/somewhere", RequestRoute.LabelFor(bare, routes));
    }

    [Fact]
    public async Task A_body_is_not_read_for_a_request_nobody_has_signed_in()
    {
        await using var server = await WeirTestServer.StartAsync();
        var context = Request();
        context.RequestServices = server.Services;
        await using var request = new ApiRequest(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => request.ReadBodyAsync());
    }

    [Fact]
    public void A_request_with_no_path_is_neither_an_upgrade_landing_nor_a_page()
    {
        var context = Request();
        context.Request.Headers.Accept = "text/html";

        Assert.False(WebApp.IsUpgradeBrowserLanding(context.Request));
        Assert.True(WebApp.IsSpaHistoryRequest(context.Request));
    }

    [Theory]
    [InlineData("readme.unknownextension", "application/octet-stream")]
    [InlineData("page.html", "text/html; charset=utf-8")]
    public void A_file_type_the_server_does_not_know_gets_the_fallback_type(string fileName, string expected)
    {
        Assert.Equal(expected, WebContentTypes.For(new FileExtensionContentTypeProvider(), fileName, "application/octet-stream"));
    }
}
