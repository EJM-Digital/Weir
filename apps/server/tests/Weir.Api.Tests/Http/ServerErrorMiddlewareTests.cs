using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Weir.Api.Http;
using Weir.Core.Configuration;

namespace Weir.Api.Tests.Http;

/// <summary>
/// The outermost error handler answers 500 itself, so <see cref="SecurityHeadersMiddleware"/> never runs for
/// that response (see <see cref="ResponseMarkers.ServerError"/>); this drives <see cref="ServerErrorMiddleware"/>
/// directly to prove it applies the same baseline headers before writing the body, and that the body is the JSON a media
/// manager can read, with nothing of the failure in it (#964).
/// </summary>
public sealed class ServerErrorMiddlewareTests
{
    [Fact]
    public async Task A_500_response_still_carries_nosniff_and_the_rest()
    {
        var context = await FailWithAsync(new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("camera=(), microphone=(), geolocation=(), payment=(), usb=()", context.Response.Headers["Permissions-Policy"]);
        Assert.Equal("default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'", context.Response.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task A_500_response_is_JSON_with_a_detail_and_the_internal_error_code()
    {
        var context = await FailWithAsync(new InvalidOperationException("boom"));

        Assert.Equal("application/json", context.Response.ContentType);
        var body = JsonNode.Parse(await BodyAsync(context))!.AsObject();
        Assert.Equal("internal_error", (string?)body["code"]);
        Assert.Equal(ServerErrorMiddleware.Detail, (string?)body["detail"]);
        Assert.Equal(2, body.Count);
        Assert.Equal(context.Response.ContentLength, (await BodyAsync(context)).Length);
    }

    [Fact]
    public async Task A_500_response_says_nothing_of_the_failure()
    {
        var failure = new InvalidOperationException("no such table: users at C:Weirdataweir.sqlite3");

        var context = await FailWithAsync(failure);

        var text = await BodyAsync(context);
        Assert.DoesNotContain("no such table", text, StringComparison.Ordinal);
        Assert.DoesNotContain("weir.sqlite3", text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), text, StringComparison.Ordinal);
    }

    private static async Task<DefaultHttpContext> FailWithAsync(Exception failure)
    {
        var home = Path.Join(Path.GetTempPath(), "weir-server-error-tests-" + Guid.NewGuid().ToString("N"));
        var options = WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = home },
            OperatingSystem.IsWindows(),
            home,
            home));
        var middleware = new ServerErrorMiddleware(_ => throw failure, options);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);
        return context;
    }

    private static async Task<string> BodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
