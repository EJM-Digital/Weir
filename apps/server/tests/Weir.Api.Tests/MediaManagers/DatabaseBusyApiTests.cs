using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weir.Api.Tests.Platform;
using Weir.Core.Configuration;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// The media-manager routes that write answer a busy database with a retryable 503 (#964). The server's write gate is held by the
/// test and its clock is a <see cref="FakeTimeProvider"/>, so a writer gives up the moment the test moves the clock past the
/// busy timeout: the whole pipeline runs with no real waiting.
/// </summary>
public sealed class DatabaseBusyApiTests
{
    private const string WebhookSecret = "s3cret";

    private static readonly Dictionary<string, string> SecretHeader = new() { ["X-Webhook-Secret"] = WebhookSecret };

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_hand_off_sent_while_the_write_gate_is_held_past_the_busy_timeout_is_answered_503_and_is_accepted_when_sent_again()
    {
        var logger = new CapturingLoggerFactory();
        var time = new FakeTimeProvider();
        await using var server = await StartAsync(logger, time);
        var watched = Path.Join(Path.GetTempPath(), "weir-busy-" + Guid.NewGuid().ToString("N"));
        var source = Path.Join(watched, "Film", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        try
        {
            await File.WriteAllTextAsync(source, "the original download");
            await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));
            var client = new ApiTestClient(server);

            HttpResponseMessage busy;
            await using (var holder = await HoldTheWriteGateAsync(server))
            {
                busy = await SendWhileHeldAsync(time, () => client.PostAsync("/api/v1/intake/webhook/deluno", Handoff(source), SecretHeader));
            }

            using (busy)
            {
                await AssertBusyAsync(busy);
            }

            using var retried = await client.PostAsync("/api/v1/intake/webhook/deluno", Handoff(source), SecretHeader);
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            AssertLoggedOnceAsAWarning(logger, "/intake/webhook/{source_key}");
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }

    [Fact]
    public async Task An_outcome_sent_while_the_write_gate_is_held_past_the_busy_timeout_is_answered_503_and_is_heard_when_sent_again()
    {
        var logger = new CapturingLoggerFactory();
        var time = new FakeTimeProvider();
        await using var server = await StartAsync(logger, time);
        var client = new ApiTestClient(server);
        var outcome = new Dictionary<string, string> { ["outcome"] = "imported", ["occurredUtc"] = "2026-10-14T09:00:00Z" };

        HttpResponseMessage busy;
        await using (var holder = await HoldTheWriteGateAsync(server))
        {
            busy = await SendWhileHeldAsync(time, () => client.PostAsync("/api/v1/intake/handoffs/deluno/h1/outcome", outcome, SecretHeader));
        }

        using (busy)
        {
            await AssertBusyAsync(busy);
        }

        // Not busy any more: the manager now hears what it would have been told with no contention, a hand-off Weir never received.
        using var retried = await client.PostAsync("/api/v1/intake/handoffs/deluno/h1/outcome", outcome, SecretHeader);
        Assert.Equal(HttpStatusCode.NotFound, retried.StatusCode);
        AssertLoggedOnceAsAWarning(logger, "/intake/handoffs/{source_key}/{handoff_id}/outcome");
    }

    [Fact]
    public async Task A_failure_that_is_not_a_busy_database_is_still_a_500_logged_as_an_unhandled_failure()
    {
        var logger = new CapturingLoggerFactory();
        await using var server = await StartAsync(logger, new FakeTimeProvider());
        await TestDatabase.ExecuteAsync(server, "PRAGMA foreign_keys=OFF; DROP TABLE media_manager_handoffs");

        using var response = await new ApiTestClient(server).PostAsync(
            "/api/v1/intake/handoffs/deluno/h1/outcome",
            new Dictionary<string, string> { ["outcome"] = "imported", ["occurredUtc"] = "2026-10-14T09:00:00Z" },
            SecretHeader);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("internal_error", body["code"]!.GetValue<string>());
        Assert.DoesNotContain("media_manager_handoffs", body.ToJsonString(), StringComparison.Ordinal);
        Assert.Single(logger.Entries, entry => entry.Message.Contains("Unhandled request failure", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("database stayed busy", StringComparison.Ordinal));
    }

    private static Task<WeirTestServer> StartAsync(ILoggerFactory logger, FakeTimeProvider time) =>
        WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", WebhookSecret)],
            configureServices: services =>
            {
                services.AddSingleton<ILoggerFactory>(logger);
                services.AddSingleton(sp => new SqliteDatabase(
                    sp.GetRequiredService<WeirOptions>().DbPath,
                    busyTimeoutMilliseconds: SqliteDatabase.BusyTimeoutMilliseconds,
                    timeProvider: time));
            });

    /// <summary>A unit of work that has begun its write transaction, and so holds the write gate until it is disposed.</summary>
    private static async Task<UnitOfWork> HoldTheWriteGateAsync(WeirTestServer server)
    {
        var holder = await UnitOfWork.OpenAsync(server.Services.GetRequiredService<SqliteDatabase>());
        await holder.BeginImmediateAsync();
        return holder;
    }

    /// <summary>
    /// Sends the request, and moves the database's clock a second at a time until the request has given up: the request reaches the gate
    /// some way into its handling, and a clock moved before then would not be waited on.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWhileHeldAsync(FakeTimeProvider time, Func<Task<HttpResponseMessage>> send)
    {
        var request = send();
        var deadline = DateTime.UtcNow + Limit;
        while (!request.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the request was still waiting for the database");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        return await request;
    }

    private static async Task AssertBusyAsync(HttpResponseMessage busy)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("application/json", busy.Content.Headers.ContentType?.MediaType);
        var retryAfter = int.Parse(Assert.Single(busy.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 30, 31);
        var body = (JsonObject)await Json(busy);
        Assert.Equal("database_busy", body["code"]!.GetValue<string>());
        Assert.Contains($"Try again in {retryAfter} seconds.", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, body.Count);
        Assert.True(busy.Headers.Contains("X-Request-ID"));
        Assert.Equal("nosniff", Assert.Single(busy.Headers.GetValues("X-Content-Type-Options")));
    }

    private static void AssertLoggedOnceAsAWarning(CapturingLoggerFactory logger, string route)
    {
        var warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("database stayed busy", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains($"route={route}", warning.Message, StringComparison.Ordinal);
        Assert.Contains("waited 3", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Unhandled request failure", StringComparison.Ordinal));
    }

    private static Dictionary<string, object> Handoff(string source) => new()
    {
        ["eventType"] = "deluno.processor-handoff",
        ["handoffId"] = "h1",
        ["libraryId"] = "lib-1",
        ["mediaType"] = "movies",
        ["sourcePath"] = source,
        ["releaseName"] = "Nosferatu.1922.1080p.BluRay-PublicHD",
        ["callbackPath"] = "/api/integrations/processors/events",
    };

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        // Background timers log through this factory too, from their own threads.
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

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
                owner.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
