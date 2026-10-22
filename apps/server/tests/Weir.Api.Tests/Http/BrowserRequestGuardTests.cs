using System.Net;
using Microsoft.AspNetCore.Http;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Http;

/// <summary>
/// What the pipeline asks of a request that comes from a browser or through a proxy: the cross-origin rules, the origin a
/// state-changing request must come from, the header that marks an API call, and the scheme a trusted proxy may set.
/// </summary>
public sealed class BrowserRequestGuardTests
{
    private const string Allowed = "https://weir.example";
    private const string RevokeOthers = "/api/v1/auth/sessions/revoke-others";

    private static Task<HttpResponseMessage> PreflightAsync(ApiTestClient client, string origin, string method, string? headers = null, bool privateNetwork = false)
    {
        var values = new Dictionary<string, string> { ["Origin"] = origin, ["Access-Control-Request-Method"] = method };
        if (headers is not null)
        {
            values["Access-Control-Request-Headers"] = headers;
        }

        if (privateNetwork)
        {
            values["Access-Control-Request-Private-Network"] = "true";
        }

        return client.SendAsync(HttpMethod.Options, "/api/v1/auth/csrf", headers: values);
    }

    [Fact]
    public async Task A_preflight_from_an_origin_that_is_not_allowed_is_refused_and_names_what_was_wrong()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));

        using var response = await PreflightAsync(new ApiTestClient(server), "https://other.example", "POST");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Disallowed CORS origin", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_preflight_names_every_part_that_was_wrong_and_asks_for_nothing_on_a_private_network()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));

        using var response = await PreflightAsync(new ApiTestClient(server), Allowed, "TRACE", "X-Injected", privateNetwork: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Disallowed CORS method, headers, private-network", await response.Content.ReadAsStringAsync());
        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_preflight_that_is_allowed_says_which_origin_may_call_with_cookies()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));

        using var response = await PreflightAsync(new ApiTestClient(server), Allowed, "PUT", "content-type, X-Requested-With");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", await response.Content.ReadAsStringAsync());
        Assert.Equal((Allowed, "true", "600"), (Header(response, "Access-Control-Allow-Origin"), Header(response, "Access-Control-Allow-Credentials"), Header(response, "Access-Control-Max-Age")));
        Assert.Equal("Origin", Header(response, "Vary"));
    }

    [Fact]
    public async Task An_answer_to_an_allowed_origin_carries_its_cors_headers_and_one_to_another_origin_does_not()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));
        var client = new ApiTestClient(server);

        using var allowed = await client.GetAsync("/api/v1/auth/csrf", new Dictionary<string, string> { ["Origin"] = Allowed });
        using var other = await client.GetAsync("/api/v1/auth/csrf", new Dictionary<string, string> { ["Origin"] = "https://other.example" });

        Assert.Equal((Allowed, "true"), (Header(allowed, "Access-Control-Allow-Origin"), Header(allowed, "Access-Control-Allow-Credentials")));
        Assert.Contains("Origin", Header(allowed, "Vary"), StringComparison.Ordinal);
        Assert.Equal("true", Header(other, "Access-Control-Allow-Credentials"));
        Assert.Equal(string.Empty, Header(other, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_allowed_origin_is_added_to_a_vary_header_the_answer_already_has()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));
        var client = new ApiTestClient(server);

        using var response = await client.GetAsync("/health", new Dictionary<string, string> { ["Origin"] = Allowed, ["Accept-Encoding"] = "gzip" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Origin", Header(response, "Vary"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_state_changing_api_call_from_a_browser_must_say_it_is_one()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var withoutHeader = await client.PostAsync(RevokeOthers, headers: new Dictionary<string, string> { ["Origin"] = "http://localhost" });

        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Equal(
            "Missing X-Requested-With header. This endpoint requires an authenticated browser session.",
            await Detail(withoutHeader));
    }

    [Fact]
    public async Task A_state_changing_call_must_come_from_a_trusted_origin_or_referer_when_some_are_set()
    {
        await using var server = await StartServerAsync(("WEIR_CORS_ORIGINS", Allowed));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        using (var signIn = await client.PostAsync(
            "/api/v1/auth/login",
            new { username = "alice", password = AdminPassword, csrf_token = await client.CsrfAsync(), trusted_device = false },
            new Dictionary<string, string> { ["Origin"] = Allowed, ["X-Requested-With"] = "XMLHttpRequest" }))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        var token = await client.CsrfAsync();

        async Task<(HttpStatusCode Status, string Detail)> RevokeWithAsync(params (string Name, string Value)[] headers)
        {
            var all = headers.ToDictionary(h => h.Name, h => h.Value);
            all["X-Requested-With"] = "XMLHttpRequest";
            all["X-CSRF-Token"] = token;
            using var response = await client.PostAsync(RevokeOthers, headers: all);
            return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? string.Empty : await Detail(response));
        }

        Assert.Equal((HttpStatusCode.Forbidden, "Origin not allowed."), await RevokeWithAsync(("Origin", "https://other.example")));
        Assert.Equal((HttpStatusCode.Forbidden, "Referer not allowed."), await RevokeWithAsync(("Referer", "https://other.example/page")));
        Assert.Equal((HttpStatusCode.Forbidden, "Missing Origin or Referer for browser POST."), await RevokeWithAsync());
        Assert.Equal((HttpStatusCode.OK, string.Empty), await RevokeWithAsync(("Origin", Allowed + "/")));
        Assert.Equal((HttpStatusCode.OK, string.Empty), await RevokeWithAsync(("Referer", Allowed + "/settings?tab=1")));
    }

    [Theory]
    [InlineData("10.0.0.5", "https", "https")]
    [InlineData("10.0.0.5", " HTTPS ", "https")]
    [InlineData("10.0.0.5", "http", "http")]
    [InlineData("10.0.0.5", "ftp", "http")]
    [InlineData("203.0.113.9", "https", "http")]
    public async Task Only_a_trusted_proxy_can_set_the_scheme_and_only_to_http_or_https(string peer, string forwardedProto, string expectedScheme)
    {
        await using var server = await StartServerAsync(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/24"));

        var context = await server.TestServer.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            request.Request.Method = "GET";
            request.Request.Path = "/health";
            request.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(expectedScheme, context.Request.Scheme);
    }

    [Fact]
    public async Task A_trusted_proxy_that_sends_no_scheme_leaves_the_scheme_alone()
    {
        await using var server = await StartServerAsync(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/24"));

        var context = await server.TestServer.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
            request.Request.Method = "GET";
            request.Request.Path = "/health";
        });

        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task A_head_request_for_a_file_of_the_web_app_gets_its_headers_and_no_body()
    {
        await using var server = await WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_WEB_DIST", "{home}/web")], prepareHome: WeirTestServer.WriteWebDist);
        var client = new ApiTestClient(server);

        using var get = await client.GetAsync("/assets/font.woff2");
        using var head = await client.SendAsync(HttpMethod.Head, "/assets/font.woff2");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
    }
}
