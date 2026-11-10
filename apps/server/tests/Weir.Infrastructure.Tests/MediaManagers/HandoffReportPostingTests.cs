using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The one POST of a hand-off report and what Weir reads from the answer: the headers and connection it goes with, the manager
/// that had already stopped waiting, how long a manager asked Weir to wait, and what Weir logs when the report does not get through.
/// </summary>
public sealed class HandoffReportPostingTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Url = "http://192.0.2.30:5099" + Callback;

    private static readonly ManagerConnection Connection = new("deluno", "rig", "http://192.0.2.30:5099", "k1", 7);

    private readonly MediaManagerFixture _fixture = new();
    private readonly TaggingHandlers _handlers;
    private readonly HandoffCompletionReporter _reporter;

    public HandoffReportPostingTests()
    {
        _handlers = new TaggingHandlers(_fixture.Http);
        _reporter = new HandoffCompletionReporter(
            _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Libraries, _handlers, _fixture.Store.Clock, _fixture.ReporterLog);
    }

    public void Dispose() => _fixture.Dispose();

    private static HandoffReportTarget Target(IReadOnlyDictionary<string, string>? headers = null) =>
        new(Connection, Url, headers ?? new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Api-Key"] = "k1" });

    private static WireObject Body() => new WireObject().Set("status", "completed");

    private static TimeSpan? Seconds(int? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    [Fact]
    public async Task A_report_goes_as_json_with_the_managers_key_and_is_marked_as_traffic_of_its_connection()
    {
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));

        var delivery = await _reporter.PostHandoffReportAsync(Target(), Body());

        Assert.True(delivery.Accepted);
        Assert.Equal("reported completed to Deluno (rig)", delivery.Status);
        var request = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.False(request.FollowRedirects);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal("k1", request.Headers["X-Api-Key"]);
        Assert.Equal(Connection.Reference, Assert.Single(_handlers.Tags));
    }

    [Fact]
    public async Task A_manager_that_says_it_had_already_stopped_waiting_is_told_apart_from_one_that_refused()
    {
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Conflict, """{"status":"dismissed"}"""));
        var dismissed = await _reporter.PostHandoffReportAsync(Target(), Body());

        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Conflict, """{"status":"running"}"""));
        var conflicting = await _reporter.PostHandoffReportAsync(Target(), Body());

        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.InternalServerError, """{"status":"dismissed"}"""));
        var broken = await _reporter.PostHandoffReportAsync(Target(), Body());

        Assert.True(dismissed.ManagerStoppedWaiting);
        Assert.False(dismissed.Accepted);
        Assert.False(conflicting.ManagerStoppedWaiting);
        Assert.False(broken.ManagerStoppedWaiting);
    }

    [Fact]
    public async Task A_report_the_manager_refuses_logs_the_address_and_the_answer_and_one_it_cannot_reach_logs_why()
    {
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable));
        var refused = await _reporter.PostHandoffReportAsync(Target(), Body());

        _fixture.Http.Throw(HttpMethod.Post, Callback, new HttpRequestException("The wire was cut."));
        var unreachable = await _reporter.PostHandoffReportAsync(Target(), Body());

        Assert.Equal("failed: Deluno (rig) answered HTTP 503", refused.Status);
        Assert.StartsWith("failed: could not reach ", unreachable.Status, StringComparison.Ordinal);
        Assert.Contains((LogLevel.Warning, $"Hand-off report to {Url} returned HTTP 503"), _fixture.ReporterLog.Messages);
        Assert.Contains((LogLevel.Warning, $"Hand-off report to {Url} failed: The wire was cut."), _fixture.ReporterLog.Messages);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_report_to_a_manager_that_never_answers_is_given_up_on_when_the_clock_has_run_the_timeout_out()
    {
        var hanging = new HangingHandlers();
        var reporter = new HandoffCompletionReporter(
            _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Libraries, hanging, _fixture.Store.Clock, _fixture.ReporterLog);

        var posting = reporter.PostHandoffReportAsync(Target(), Body());
        await hanging.Arrived.Task;
        _fixture.Store.Clock.Advance(CompletionReports.Timeout - TimeSpan.FromSeconds(1));
        Assert.False(posting.IsCompleted);
        _fixture.Store.Clock.Advance(TimeSpan.FromSeconds(1));
        var delivery = await posting;

        Assert.False(delivery.Accepted);
        Assert.StartsWith("failed: could not reach ", delivery.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", null)]
    public async Task A_manager_that_asks_to_wait_a_number_of_seconds_is_waited_for_and_one_that_asks_for_none_is_not(string header, int? seconds)
    {
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.TooManyRequests, null, ("Retry-After", header)));

        var delivery = await _reporter.PostHandoffReportAsync(Target(), Body());

        Assert.Equal(Seconds(seconds), delivery.RetryAfter);
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(-90, null)]
    [InlineData(0, null)]
    public async Task A_manager_that_names_the_time_it_will_be_ready_is_waited_for_until_then_unless_that_has_passed(int secondsFromNow, int? waitSeconds)
    {
        var now = _fixture.Store.Clock.GetUtcNow();
        var at = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddSeconds(secondsFromNow);
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.TooManyRequests, null, ("Retry-After", at.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));

        var delivery = await _reporter.PostHandoffReportAsync(Target(), Body());

        Assert.Equal(Seconds(waitSeconds), delivery.RetryAfter);
    }

    [Fact]
    public void A_status_that_begins_with_the_managers_answer_is_still_read_as_one()
    {
        Assert.True(HandoffCompletionReporter.IsTransientFailure(new HandoffReportDelivery(false, "answered HTTP 503")));
        Assert.False(HandoffCompletionReporter.IsTransientFailure(new HandoffReportDelivery(false, "answered HTTP 404")));
        Assert.False(HandoffCompletionReporter.IsTransientFailure(new HandoffReportDelivery(false, "dismissed")));
    }

    [Fact]
    public async Task A_report_for_a_connection_with_a_key_carries_it_as_json()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");

        var (keyed, _) = await _fixture.Db(uow => _reporter.ResolveHandoffTargetAsync(uow, new HandoffOrigin("deluno", "h1", Callback, "Some.Film", null)), commit: false);

        Assert.Equal("application/json", keyed!.Headers["Content-Type"]);
        Assert.Equal("k1", keyed.Headers["X-Api-Key"]);
        Assert.Equal("k1", keyed.Connection.ApiKey);
        Assert.Equal($"http://192.0.2.30:5099{Callback}", keyed.Url);
    }

    [Fact]
    public async Task A_report_for_a_connection_with_no_key_goes_with_no_key()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", apiKey: null);

        var (open, _) = await _fixture.Db(uow => _reporter.ResolveHandoffTargetAsync(uow, new HandoffOrigin("deluno", "h1", Callback, "Some.Film", null)), commit: false);

        Assert.Equal("application/json", open!.Headers["Content-Type"]);
        Assert.False(open.Headers.ContainsKey("X-Api-Key"));
        Assert.Equal(string.Empty, open.Connection.ApiKey);
    }

    [Fact]
    public async Task A_hand_off_whose_sending_connection_is_switched_off_says_its_report_waits_for_it()
    {
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1", enabled: false);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.31:5099", "k2");
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", null, "Film/film.mkv", connection);
            return 0;
        });

        var (target, reason) = await _fixture.Db(uow => _reporter.ResolveHandoffTargetAsync(uow, new HandoffOrigin("deluno", "h1", Callback, null)), commit: false);

        Assert.Null(target);
        Assert.Equal("the deluno connection that sent this hand-off is switched off or was removed, so its report waits for it", reason);
    }

    /// <summary>Hands out the fake manager's handlers, noting which connection each request was marked as belonging to.</summary>
    private sealed class TaggingHandlers(FakeManagerHttp inner) : IManagerHttpHandlerFactory
    {
        public List<ConnectionRef> Tags { get; } = [];

        public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) =>
            new Tagging(inner.Handler(followRedirects, policy), Tags);

        private sealed class Tagging(HttpMessageHandler next, List<ConnectionRef> tags) : DelegatingHandler(next)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (ConnectionTag.TryRead(request, out var connection))
                {
                    tags.Add(connection);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>A manager that takes the request and never answers it.</summary>
    private sealed class HangingHandlers : IManagerHttpHandlerFactory
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) => new Hanging(Arrived);

        private sealed class Hanging(TaskCompletionSource arrived) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                arrived.TrySetResult();
                var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
                return await never.Task;
            }
        }
    }
}
