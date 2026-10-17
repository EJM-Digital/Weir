using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Metrics;

namespace Weir.Api.Tests.Http;

/// <summary>
/// A browser that leaves mid-request (a page change, a closed tab) cancels the work Weir was doing for it. That is not a
/// failure: <see cref="RequestContextMiddleware"/> keeps it out of the log's warnings and errors, and a cancellation that
/// nobody asked for is still an error.
/// </summary>
public sealed class RequestContextMiddlewareAbortTests
{
    public static TheoryData<Exception> ConnectionGoneFailures => new()
    {
        new TaskCanceledException(),
        new IOException("The client reset the request stream."),
        new BadHttpRequestException("Unexpected end of request content."),
    };

    [Theory]
    [MemberData(nameof(ConnectionGoneFailures))]
    public async Task A_request_the_client_abandoned_logs_nothing_at_warning_or_above_and_is_not_rethrown(Exception failure)
    {
        var logger = new CapturingLoggerFactory();
        using var aborted = new CancellationTokenSource();
        var context = NewContext(aborted.Token);
        var middleware = NewMiddleware(logger, async _ =>
        {
            await aborted.CancelAsync();
            throw failure;
        });

        await middleware.InvokeAsync(context);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task A_response_that_had_already_started_when_the_client_left_is_not_an_error_either()
    {
        var logger = new CapturingLoggerFactory();
        using var aborted = new CancellationTokenSource();
        var context = NewContext(aborted.Token);
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = NewMiddleware(logger, async _ =>
        {
            await aborted.CancelAsync();
            throw new IOException("The client closed the connection while the response was being written.");
        });

        await middleware.InvokeAsync(context);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task An_abandoned_request_is_counted_without_adding_to_the_latency_figures()
    {
        var metrics = new RuntimeMetricsStore(TimeProvider.System);
        using var aborted = new CancellationTokenSource();
        var middleware = NewMiddleware(new CapturingLoggerFactory(), async _ =>
        {
            await Task.Yield();
            await aborted.CancelAsync();
            throw new TaskCanceledException();
        }, metrics);

        await middleware.InvokeAsync(NewContext(aborted.Token));

        Assert.Equal(1, metrics.GetSummary().TotalRequests);
        var figures = metrics.GetRequestFigures(DateTimeOffset.MinValue);
        Assert.Equal(0, figures.MedianMs);
        Assert.Equal(0, figures.P95Ms);
        Assert.Equal(0, figures.ServerErrors);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_still_an_unhandled_failure()
    {
        var logger = new CapturingLoggerFactory();
        var middleware = NewMiddleware(logger, _ => throw new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => middleware.InvokeAsync(NewContext(CancellationToken.None)));

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Unhandled request failure", StringComparison.Ordinal));
    }

    private static DefaultHttpContext NewContext(CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };
        context.Request.Path = new PathString("/api/v1/auth/me");
        context.Request.Method = "GET";
        return context;
    }

    private static RequestContextMiddleware NewMiddleware(ILoggerFactory loggerFactory, RequestDelegate next, RuntimeMetricsStore? metrics = null) =>
        new(next, loggerFactory, metrics ?? new RuntimeMetricsStore(TimeProvider.System), new RouteTable(), TimeProvider.System);

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
