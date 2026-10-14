using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Configuration;
using Weir.Core.Metrics;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests.Http;

/// <summary>
/// A write that gave up on a busy database is answered 503 with a <c>Retry-After</c> and a <c>database_busy</c> code by one
/// middleware, whatever route it was on, and is logged once as a warning, never as the unhandled failure the request context logs for
/// an exception it sees pass (#964).
/// </summary>
public sealed class DatabaseBusyMiddlewareTests
{
    private const string Route = "/intake/handoffs/{source_key}/{handoff_id}/outcome";
    private const string RequestPath = "/api/v1/intake/handoffs/deluno/h1/outcome";

    public static TheoryData<double, int> RetryAfterFigures => new()
    {
        { 0, 1 },
        { 0.2, 1 },
        { 1.4, 1 },
        { 1.5, 2 },
        { 2, 2 },
        { 30, 30 },
        { 30.4, 30 },
        { 59.6, 60 },
        { 600, 60 },
    };

    [Theory]
    [MemberData(nameof(RetryAfterFigures))]
    public void Retry_after_is_the_wait_in_whole_seconds_kept_between_one_and_sixty(double waitedSeconds, int expected) =>
        Assert.Equal(expected, DatabaseBusyMiddleware.RetryAfterSeconds(TimeSpan.FromSeconds(waitedSeconds)));

    [Fact]
    public async Task A_writer_that_gave_up_after_thirty_seconds_is_answered_503_asking_for_thirty()
    {
        var logger = new CapturingLoggerFactory();

        var context = await RunAsync(logger, new DatabaseBusyException(TimeSpan.FromSeconds(30)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("30", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("application/json", context.Response.ContentType);
        var body = await BodyAsync(context);
        Assert.Equal("database_busy", (string?)body["code"]);
        Assert.Equal("Weir is busy with other changes and could not finish this request. Try again in 30 seconds.", (string?)body["detail"]);
        Assert.Equal(2, body.Count);
    }

    [Fact]
    public async Task The_wait_asked_for_is_the_wait_measured_not_the_busy_timeout()
    {
        var context = await RunAsync(new CapturingLoggerFactory(), new DatabaseBusyException(TimeSpan.FromSeconds(4.2)));

        Assert.Equal("4", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("Weir is busy with other changes and could not finish this request. Try again in 4 seconds.", (string?)(await BodyAsync(context))["detail"]);
    }

    [Fact]
    public async Task A_wait_of_one_second_is_asked_for_in_the_singular()
    {
        var context = await RunAsync(new CapturingLoggerFactory(), new DatabaseBusyException(TimeSpan.FromSeconds(1)));

        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        Assert.EndsWith("Try again in 1 second.", (string?)(await BodyAsync(context))["detail"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task A_busy_answer_from_inside_a_statement_is_503_and_asks_for_the_busy_timeout(int sqliteCode)
    {
        var context = await RunAsync(new CapturingLoggerFactory(), new SqliteException("database is locked", sqliteCode), busyTimeoutSeconds: 7);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("7", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("database_busy", (string?)(await BodyAsync(context))["code"]);
    }

    [Fact]
    public async Task A_busy_extended_code_is_still_busy()
    {
        var context = await RunAsync(new CapturingLoggerFactory(), new SqliteException("database is locked", 5, 261));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_busy_database_is_logged_once_at_warning_with_the_route_and_the_wait()
    {
        var logger = new CapturingLoggerFactory();

        await RunAsync(logger, new DatabaseBusyException(TimeSpan.FromSeconds(30)));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("waited 30.0 seconds", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"route={Route}", entry.Message, StringComparison.Ordinal);
        Assert.Contains("method=POST", entry.Message, StringComparison.Ordinal);
        Assert.Contains("503", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_busy_database_is_not_logged_as_an_unhandled_failure_by_the_request_context_around_it()
    {
        var logger = new CapturingLoggerFactory();
        var routes = Routes();
        var metrics = new RuntimeMetricsStore(TimeProvider.System);
        var busy = new DatabaseBusyMiddleware(_ => throw new DatabaseBusyException(TimeSpan.FromSeconds(30)), logger, routes, Options());
        var outer = new RequestContextMiddleware(busy.InvokeAsync, logger, metrics, routes, TimeProvider.System);
        var context = NewContext();

        await outer.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Unhandled request failure", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(1, metrics.GetSummary().TotalRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(14)]
    public async Task A_SQLite_failure_that_is_not_a_busy_database_is_left_to_the_error_handler(int sqliteCode)
    {
        var failure = new SqliteException("something else", sqliteCode);
        var middleware = new DatabaseBusyMiddleware(_ => throw failure, new CapturingLoggerFactory(), Routes(), Options());

        var raised = await Assert.ThrowsAsync<SqliteException>(() => middleware.InvokeAsync(NewContext()));

        Assert.Same(failure, raised);
    }

    [Fact]
    public async Task A_failure_that_is_not_SQLite_is_left_to_the_error_handler()
    {
        var failure = new InvalidOperationException("boom");
        var middleware = new DatabaseBusyMiddleware(_ => throw failure, new CapturingLoggerFactory(), Routes(), Options());

        var raised = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(NewContext()));

        Assert.Same(failure, raised);
    }

    [Fact]
    public async Task A_response_that_has_started_cannot_be_changed_so_the_failure_is_left_to_the_error_handler()
    {
        var context = NewContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = new DatabaseBusyMiddleware(_ => throw new DatabaseBusyException(TimeSpan.FromSeconds(30)), new CapturingLoggerFactory(), Routes(), Options());

        await Assert.ThrowsAsync<DatabaseBusyException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task A_request_the_client_abandoned_is_not_answered()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = NewContext(aborted.Token);
        var logger = new CapturingLoggerFactory();
        var middleware = new DatabaseBusyMiddleware(_ => throw new DatabaseBusyException(TimeSpan.FromSeconds(30)), logger, Routes(), Options());

        await Assert.ThrowsAsync<DatabaseBusyException>(() => middleware.InvokeAsync(context));

        Assert.Empty(logger.Entries);
    }

    private static async Task<DefaultHttpContext> RunAsync(CapturingLoggerFactory logger, Exception failure, int busyTimeoutSeconds = 30)
    {
        var middleware = new DatabaseBusyMiddleware(_ => throw failure, logger, Routes(), Options(busyTimeoutSeconds));
        var context = NewContext();
        await middleware.InvokeAsync(context);
        return context;
    }

    private static async Task<JsonObject> BodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonNode.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync())!.AsObject();
    }

    private static DefaultHttpContext NewContext(CancellationToken aborted = default)
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            RequestAborted = aborted,
        };
        context.Request.Method = "POST";
        context.Request.Path = RequestPath;
        return context;
    }

    private static RouteTable Routes()
    {
        var routes = new RouteTable();
        routes.Add("/api/v1" + Route, ["POST"], Route);
        return routes;
    }

    private static WeirOptions Options(int busyTimeoutSeconds = 30)
    {
        var home = Path.Join(Path.GetTempPath(), "weir-database-busy-tests-" + Guid.NewGuid().ToString("N"));
        return WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = home,
                ["WEIR_SQLITE_BUSY_TIMEOUT_SECONDS"] = busyTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            OperatingSystem.IsWindows(),
            home,
            home));
    }

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
