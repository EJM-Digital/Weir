using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Configuration;
using Weir.Core.Metrics;

namespace Weir.Api.Tests.Http;

/// <summary>The small request middleware that sit in front of every route, run on their own against a request they are given.</summary>
public sealed class ProtocolMiddlewareTests
{
    private static WeirOptions Options(string trustedProxies = "")
    {
        var home = Path.Join(Path.GetTempPath(), "weir-protocol-middleware-tests-" + Guid.NewGuid().ToString("N"));
        return WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = home, ["WEIR_TRUSTED_PROXY_IPS"] = trustedProxies },
            OperatingSystem.IsWindows(),
            home,
            home));
    }

    [Fact]
    public void A_request_with_no_peer_address_has_no_client_host()
    {
        var context = new DefaultHttpContext();

        Assert.Null(ApiRequest.DefaultClientHost(context));
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8::7", "2001:db8::7")]
    public void The_client_host_is_the_peer_address_with_an_ipv4_one_shown_as_ipv4(string peer, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);

        Assert.Equal(expected, ApiRequest.DefaultClientHost(context));
    }

    [Fact]
    public async Task A_proxy_header_from_a_peer_with_no_address_is_not_believed()
    {
        var middleware = new TrustedProxySchemeMiddleware(_ => Task.CompletedTask, Options("10.0.0.0/24"));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.InvokeAsync(context);

        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task A_proxy_header_from_a_trusted_peer_sets_the_scheme_when_the_connection_was_not_marked_by_the_pipeline()
    {
        var middleware = new TrustedProxySchemeMiddleware(_ => Task.CompletedTask, Options("10.0.0.0/24"));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        context.Request.Headers["X-Forwarded-Proto"] = new[] { "http", "https" };

        await middleware.InvokeAsync(context);

        Assert.Equal("https", context.Request.Scheme);
    }

    [Theory]
    [InlineData("GET", "/api/v1/auth/me", "http://other.example", false)]
    [InlineData("POST", "/health", "http://other.example", false)]
    [InlineData("POST", "/api/v1/auth/logout", null, false)]
    [InlineData("POST", "/API/v1/auth/logout", "http://other.example", true)]
    [InlineData("DELETE", "/api/v1/processing/libraries/1", "http://other.example", true)]
    public async Task A_state_changing_api_call_with_an_origin_and_no_marker_is_the_only_one_refused(string method, string path, string? origin, bool refused)
    {
        var reached = false;
        var middleware = new XRequestedWithMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;
        context.Request.Path = path;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        await middleware.InvokeAsync(context);

        Assert.Equal(refused, !reached);
        Assert.Equal(refused ? StatusCodes.Status403Forbidden : StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_head_request_runs_as_a_get_and_whatever_the_route_sends_goes_nowhere_while_the_response_still_starts_and_ends()
    {
        var original = new RecordingBodyFeature();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Features.Set<IHttpResponseBodyFeature>(original);
        string? methodSeen = null;
        var middleware = new HeadMirrorsGetMiddleware(async inner =>
        {
            methodSeen = inner.Request.Method;
            var feature = inner.Features.Get<IHttpResponseBodyFeature>()!;
            await feature.Stream.WriteAsync("never sent"u8.ToArray());
            await feature.Writer.WriteAsync("never sent either"u8.ToArray());
            feature.DisableBuffering();
            await feature.StartAsync();
            await feature.SendFileAsync("film.mkv", 0, null);
            await feature.CompleteAsync();
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(HttpMethods.Get, methodSeen);
        Assert.Equal(HttpMethods.Head, context.Request.Method);
        Assert.Same(original, context.Features.Get<IHttpResponseBodyFeature>());
        Assert.Equal(["DisableBuffering", "Start", "Start", "Complete"], original.Calls);
        Assert.Equal(0, original.BytesWritten);
    }

    [Theory]
    [InlineData(LogLevel.Information, LogLevel.Information, true)]
    [InlineData(LogLevel.Error, LogLevel.Warning, true)]
    [InlineData(LogLevel.Debug, LogLevel.Information, false)]
    [InlineData(LogLevel.None, LogLevel.Trace, false)]
    public void A_log_record_is_counted_by_level_when_it_is_at_or_above_the_minimum(LogLevel level, LogLevel minimum, bool counted)
    {
        var metrics = new RuntimeMetricsStore(TimeProvider.System);
        using var provider = new MetricsLoggerProvider(metrics, minimum);
        var logger = provider.CreateLogger("weir.test");

        logger.Log(level, new EventId(1), "state", null, (state, _) => state);

        Assert.Equal(counted, logger.IsEnabled(level));
        Assert.Equal(counted ? 1 : 0, metrics.GetSummary().LogCounts.Values.Sum());
        Assert.Null(logger.BeginScope("scope"));
    }

    /// <summary>A response body feature that records which calls reached it instead of sending anything.</summary>
    private sealed class RecordingBodyFeature : IHttpResponseBodyFeature
    {
        public List<string> Calls { get; } = [];

        public long BytesWritten { get; private set; }

        public Stream Stream { get; } = new MemoryStream();

        public System.IO.Pipelines.PipeWriter Writer => System.IO.Pipelines.PipeWriter.Create(Stream);

        public void DisableBuffering() => Calls.Add("DisableBuffering");

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            Calls.Add("SendFile");
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Calls.Add("Complete");
            BytesWritten += Stream.Length;
            return Task.CompletedTask;
        }
    }
}
