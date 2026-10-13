using System.Net;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The start-up wait for a server's /ready: it pauses between attempts, frees every response, waits for as long as the server
/// keeps reporting progress and ends in a <see cref="ServerStartHungException"/> or <see cref="ServerExitedException"/>, never a
/// network exception. Time is fake and every step waits on a signal, so nothing here depends on the machine's speed.
/// </summary>
public sealed class ServerHealthTests
{
    private const int TestTimeoutMs = 10_000;
    private const int ServerPid = 5764;
    private static readonly Uri ReadyUrl = new("http://127.0.0.1:9347/ready");
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);
    private static readonly StartPolicy Policy = StartPolicy.Default;
    private static readonly DateTimeOffset ProcessStarted = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Each_retry_waits_the_pause_before_asking_again()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(call => call < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock);
        await handler.NextCall();
        Assert.Equal(Pause, await clock.NextDelay());
        Assert.Equal(1, handler.Calls);
        clock.Advance(Pause);
        await handler.NextCall();
        Assert.Equal(Pause, await clock.NextDelay());
        clock.Advance(Pause);
        await waiting;

        Assert.Equal(3, handler.Calls);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Every_response_is_freed()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(call => call < 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock);
        await clock.NextDelay();
        clock.Advance(Pause);
        await waiting;

        Assert.All(handler.Responses, response => Assert.True(response.Disposed));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_reports_nothing_and_answers_not_ready_is_hung_after_the_first_progress_window()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(_ => HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock);
        await DriveUntilDone(waiting, clock);

        var error = await Assert.ThrowsAsync<ServerStartHungException>(() => waiting);
        Assert.Contains("HTTP 503", error.Message);
        Assert.Contains("before it reported anything", error.Message);
        Assert.Equal(Policy.FirstProgressWindow, error.Verdict.SilentFor);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Nothing_listening_ends_in_a_hung_start_not_a_network_error()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("No connection could be made"));
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock);
        await DriveUntilDone(waiting, clock);

        var error = await Assert.ThrowsAsync<ServerStartHungException>(() => waiting);
        Assert.Contains("No connection could be made", error.Message);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_keeps_reporting_progress_is_waited_for_past_every_window_until_it_is_ready()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(call => call < 4_000 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var reports = 0;

        // 4,000 asks a quarter of a second apart are over sixteen minutes; a new report comes every tenth.
        var waiting = Wait(client, clock, () => Report("migrating", step: ++reports / 10, steps: 400));
        await DriveUntilDone(waiting, clock);

        await waiting;
        Assert.Equal(4_000, handler.Calls);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_reported_and_then_went_quiet_is_hung_after_the_stall_window_in_its_last_phase()
    {
        var clock = new DelayWatchingTimeProvider();
        var handler = new ScriptedHandler(_ => HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock, () => Report("opening_database"));
        await DriveUntilDone(waiting, clock);

        var error = await Assert.ThrowsAsync<ServerStartHungException>(() => waiting);
        Assert.Contains("opening the database", error.Message);
        Assert.Equal(Policy.StallWindow, error.Verdict.SilentFor);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_becomes_ready_at_the_moment_it_would_have_been_called_hung_is_ready()
    {
        var clock = new DelayWatchingTimeProvider();
        var asks = (int)(Policy.FirstProgressWindow / Pause) + 1;
        var handler = new ScriptedHandler(call => call < asks ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var waiting = Wait(client, clock);
        await DriveUntilDone(waiting, clock);

        await waiting;
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_exited_fails_at_once_with_its_exit_code()
    {
        var handler = new ScriptedHandler(_ => HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var clock = new FakeTimeProvider();

        var error = await Assert.ThrowsAsync<ServerExitedException>(() =>
            ServerHealth.WaitUntilReadyAsync(client, ReadyUrl, () => 3, Watch(clock), () => (null, null), Pause, clock, CancellationToken.None));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains("code 3", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    private static StartWatch Watch(TimeProvider clock) => new(Policy, clock, ServerPid);

    private static StartProgress Report(string phase, int step = 0, int steps = 0) =>
        new(ServerPid, phase, step, steps, TimeSpan.Zero, ProcessStarted + TimeSpan.FromSeconds(1));

    private static Task Wait(HttpClient client, TimeProvider clock, Func<StartProgress?>? progress = null) =>
        ServerHealth.WaitUntilReadyAsync(client, ReadyUrl, () => null, Watch(clock), () => (progress?.Invoke(), null), Pause, clock, CancellationToken.None);

    // Lets each pause pass as soon as the wait starts it, until the wait ends.
    private static async Task DriveUntilDone(Task waiting, DelayWatchingTimeProvider clock)
    {
        while (await Task.WhenAny(waiting, clock.NextDelay()) != waiting)
        {
            clock.Advance(Pause);
        }
    }

    private sealed class ScriptedHandler(Func<int, HttpStatusCode> answer) : HttpMessageHandler
    {
        private readonly Channel<int> _calls = Channel.CreateUnbounded<int>();
        private int _count;

        public int Calls => Volatile.Read(ref _count);

        public List<TrackedResponse> Responses { get; } = [];

        public Task<int> NextCall() => _calls.Reader.ReadAsync().AsTask();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _count);
            _calls.Writer.TryWrite(call);
            var response = new TrackedResponse(answer(call));
            lock (Responses)
            {
                Responses.Add(response);
            }
            return Task.FromResult<HttpResponseMessage>(response);
        }
    }

    private sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
