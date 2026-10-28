using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Tests.Platform;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// A media manager that gives up on a hand-off (its own timeout) has gone away, which is not a failure of Weir's: the request is
/// answered with nothing, and the log says the caller left, at information, not as an unhandled failure. Deluno then sends the same
/// hand-off again, with the same <c>Idempotency-Key</c>, after 1, 2 and 5 minutes; Weir takes it in exactly once, whether the first
/// send never got as far as being taken in or was taken in and only its answer was lost.
/// </summary>
public sealed class IntakeCallerGoneApiTests : IAsyncLifetime, IDisposable
{
    private const string WebhookSecret = "s3cret";
    private const string CallerGoneHeader = "X-Test-Caller-Gone";
    private const string RemuxJobs = "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'";
    private const string IdempotencyKey = "gone-1:7";

    private readonly CapturingLoggerFactory _logger = new();
    private readonly string _watched = Path.Join(Path.GetTempPath(), "weir-caller-gone-" + Guid.NewGuid().ToString("N"));
    private WeirTestServer _server = null!;
    private string _source = null!;

    public async Task InitializeAsync()
    {
        _server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", WebhookSecret)],
            configureServices: services =>
            {
                services.AddSingleton<ILoggerFactory>(_logger);
                services.AddSingleton<IStartupFilter, CallerGoneStartupFilter>();
            });
        _source = Path.Join(_watched, "Film", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        await File.WriteAllTextAsync(_source, "the original download");
        await TestDatabase.ExecuteAsync(_server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", _watched));
    }

    public void Dispose() => _logger.Dispose();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_watched, recursive: true);
    }

    [Fact]
    public async Task A_hand_off_whose_caller_has_gone_is_logged_as_the_caller_leaving_and_not_as_a_failure()
    {
        using var response = await _server.Client.SendAsync(HandOff(callerGone: true));

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning && entry.Message.Contains("Unhandled", StringComparison.Ordinal));
        var left = Assert.Single(_logger.Entries, entry => entry.Message.StartsWith("Request abandoned by the client", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, left.Level);
        Assert.Contains("route=/intake/webhook/{source_key}", left.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_send_that_was_abandoned_leaves_nothing_behind_and_the_retry_with_its_key_is_taken_in_once()
    {
        using (await _server.Client.SendAsync(HandOff(callerGone: true)))
        {
        }

        Assert.Equal(0, await TestDatabase.ScalarAsync(_server, RemuxJobs));

        using var retried = await _server.Client.SendAsync(HandOff(callerGone: false));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync(_server, RemuxJobs));
    }

    [Fact]
    public async Task A_send_taken_in_whose_answer_was_lost_is_not_taken_in_again_when_it_is_retried_with_the_same_key()
    {
        using var first = await _server.Client.SendAsync(HandOff(callerGone: false));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        foreach (var attempt in new[] { 1, 2, 3 })
        {
            using var retried = await _server.Client.SendAsync(HandOff(callerGone: false));

            Assert.True(retried.StatusCode == HttpStatusCode.OK, $"Retry {attempt} was answered {(int)retried.StatusCode}.");
            Assert.Equal(1, await TestDatabase.ScalarAsync(_server, RemuxJobs));
        }
    }

    private HttpRequestMessage HandOff(bool callerGone)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/intake/webhook/deluno")
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["eventType"] = "deluno.processor-handoff",
                ["handoffId"] = "gone-1",
                ["libraryId"] = "lib-1",
                ["mediaType"] = "movies",
                ["sourcePath"] = _source,
                ["releaseName"] = "Nosferatu.1922.1080p.BluRay-PublicHD",
                ["callbackPath"] = "/api/integrations/processors/events",
            }),
        };
        request.Headers.Add("X-Webhook-Secret", WebhookSecret);
        request.Headers.Add("Idempotency-Key", IdempotencyKey);
        if (callerGone)
        {
            request.Headers.Add(CallerGoneHeader, "1");
        }

        return request;
    }

    /// <summary>Makes a request marked with <see cref="CallerGoneHeader"/> one whose caller has already gone, as <c>HttpContext.RequestAborted</c> says it.</summary>
    private sealed class CallerGoneStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey(CallerGoneHeader))
                {
                    context.RequestAborted = new CancellationToken(canceled: true);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

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
