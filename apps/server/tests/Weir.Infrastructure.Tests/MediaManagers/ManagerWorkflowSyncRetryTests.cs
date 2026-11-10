using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A newly connected manager whose folders are not ready yet (it is not answering, or has not reported its libraries) is looked
/// at again after a few seconds instead of waiting for the five-minute task. The clock is the test's: each pause is a timer the
/// test sees being set and moves on, so nothing here waits in real time.
/// </summary>
public sealed class ManagerWorkflowSyncRetryTests : IDisposable
{
    private const string Manifest = "/api/integrations/external/manifest";
    private const string Destinations = "/api/integrations/processors/download-destinations";
    private const string Reported = """{"libraries":[{"id":"lib-movies","name":"Movies","mediaType":"movie","rootPath":"/data/library/lib-movies","importWorkflow":"refine-before-import","processorOutputPath":"/data/ready/movies","downloadsPath":""}]}""";

    private readonly MediaManagerFixture _fixture = new();
    private readonly TimerWatchingClock _clock;
    private readonly CapturingLogger<ManagerWorkflowSync> _log = new();
    private readonly ManagerWorkflowSync _sync;
    private int _manifestCalls;
    private volatile bool _ready;
    private volatile bool _answering = true;

    public ManagerWorkflowSyncRetryTests()
    {
        _clock = new TimerWatchingClock(_fixture.Store.Clock);
        _sync = new ManagerWorkflowSync(
            _fixture.Store.Database, _fixture.ConnectionStore, _fixture.Connections, _fixture.Http, _fixture.Libraries,
            new ScanSettingsChanges(), _fixture.Store.Options, _clock, _log);
        _fixture.Http
            .Json(HttpMethod.Get, Destinations, "{}", HttpStatusCode.NotFound)
            .Route(HttpMethod.Get, Manifest, _ =>
            {
                Interlocked.Increment(ref _manifestCalls);
                return !_answering
                    ? FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable)
                    : FakeManagerHttp.Response(HttpStatusCode.OK, _ready ? Reported : """{"libraries":[]}""");
            });
    }

    public void Dispose()
    {
        _sync.Dispose();
        _fixture.Dispose();
    }

    private int ManifestCalls => Volatile.Read(ref _manifestCalls);

    private Task<long> ConnectAsync() => _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");

    private async Task<bool> HasManagerWorkflowAsync() =>
        (await _fixture.Db(uow => _fixture.Libraries.ListAsync(uow))).Any(workflow => workflow.DiscoveredLibraryKey is not null);

    /// <summary>Waits until the loop is parked on its <paramref name="timers"/>th pause, which is also when it has said so in the log.</summary>
    private Task ParkedAsync(int timers) => Eventually.ThatAsync(() => _clock.TimersSet >= timers);

    private bool Said(string text) => _log.Logged(LogLevel.Information, text);

    [Fact]
    public async Task Libraries_reported_after_a_delay_become_workflows_one_retry_later_not_five_minutes_later()
    {
        await ConnectAsync();

        _sync.RequestSync();
        await ParkedAsync(1);
        Assert.False(await HasManagerWorkflowAsync());
        Assert.True(Said("look again in 5 seconds"));

        _ready = true;
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _sync.Drained;

        Assert.True(await HasManagerWorkflowAsync());
        Assert.Equal(2, ManifestCalls);
        Assert.Equal(1, _clock.TimersSet);
    }

    [Fact]
    public async Task A_manager_that_is_not_answering_is_looked_at_again_after_5_then_10_then_every_15_seconds()
    {
        _answering = false;
        await ConnectAsync();

        _sync.RequestSync();
        string[] expected = ["5", "10", "15", "15"];
        for (var step = 0; step < expected.Length; step++)
        {
            await ParkedAsync(step + 1);
            Assert.True(Said($"look again in {expected[step]} seconds"), expected[step]);
            if (step == expected.Length - 1)
            {
                _answering = true;
                _ready = true;
            }

            _clock.Advance(TimeSpan.FromSeconds(int.Parse(expected[step], System.Globalization.CultureInfo.InvariantCulture)));
        }

        await _sync.Drained;

        Assert.True(await HasManagerWorkflowAsync());
    }

    [Fact]
    public async Task The_retries_stop_after_five_minutes_and_leave_the_rest_to_the_five_minute_task()
    {
        await ConnectAsync();
        _sync.RequestSync();
        var advanced = TimeSpan.Zero;
        var timers = 0;
        while (true)
        {
            await Eventually.ThatAsync(() => _clock.TimersSet > timers || _sync.Drained.IsCompleted);
            if (_sync.Drained.IsCompleted)
            {
                break;
            }

            timers = _clock.TimersSet;
            var pause = timers switch { 1 => 5, 2 => 10, _ => 15 };
            advanced += TimeSpan.FromSeconds(pause);
            _clock.Advance(TimeSpan.FromSeconds(pause));
        }

        Assert.Equal(TimeSpan.FromMinutes(5), advanced);
        Assert.Equal(21, _clock.TimersSet);
        Assert.Equal(22, ManifestCalls);
    }

    [Fact]
    public async Task The_retries_stop_as_soon_as_workflows_exist_even_with_time_left()
    {
        await ConnectAsync();
        _sync.RequestSync();
        await ParkedAsync(1);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ParkedAsync(2);

        _ready = true;
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _sync.Drained;
        var calls = ManifestCalls;
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.True(await HasManagerWorkflowAsync());
        Assert.Equal(2, _clock.TimersSet);
        Assert.Equal(calls, ManifestCalls);
    }

    [Fact]
    public async Task A_workflow_that_exists_before_the_request_means_no_retries_at_all()
    {
        _ready = true;
        await ConnectAsync();

        _sync.RequestSync();
        await _sync.Drained;

        Assert.True(await HasManagerWorkflowAsync());
        Assert.Equal(0, _clock.TimersSet);
        Assert.Equal(1, ManifestCalls);
    }

    [Fact]
    public async Task The_retries_stop_when_the_connection_is_disabled()
    {
        var connection = await ConnectAsync();
        _sync.RequestSync();
        await ParkedAsync(1);
        var calls = ManifestCalls;

        await _fixture.Store.Execute($"UPDATE media_manager_connections SET enabled = 0 WHERE id = {connection}");
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _sync.Drained;

        Assert.Equal(1, _clock.TimersSet);
        Assert.Equal(calls, ManifestCalls);
    }

    [Fact]
    public async Task The_retries_stop_when_the_connection_is_removed()
    {
        var connection = await ConnectAsync();
        _sync.RequestSync();
        await ParkedAsync(1);
        var calls = ManifestCalls;

        await _fixture.Db(async uow =>
        {
            await _fixture.ConnectionStore.DeleteAsync(uow, connection);
            return true;
        });
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _sync.Drained;

        Assert.Equal(1, _clock.TimersSet);
        Assert.Equal(calls, ManifestCalls);
    }

    [Fact]
    public async Task The_retries_stop_when_the_server_stops()
    {
        await ConnectAsync();
        _sync.RequestSync();
        await ParkedAsync(1);

        _sync.Dispose();
        await _sync.Drained;

        Assert.Equal(1, ManifestCalls);
        _sync.RequestSync();
        await _sync.Drained;
        Assert.Equal(1, ManifestCalls);
    }

    [Fact]
    public async Task A_new_request_while_waiting_restarts_the_one_loop_instead_of_starting_another()
    {
        await ConnectAsync();
        _sync.RequestSync();
        await ParkedAsync(1);

        _sync.RequestSync();
        await ParkedAsync(2);

        // The request ended the wait with one pass and the same loop began its short waits again from the first.
        Assert.Equal(2, ManifestCalls);
        Assert.Equal(2, _log.Messages.Count(message => message.Message.Contains("look again in 5 seconds", StringComparison.Ordinal)));
        _sync.RequestSync();
        await ParkedAsync(3);
        Assert.Equal(3, ManifestCalls);
        Assert.Equal(3, _log.Messages.Count(message => message.Message.Contains("look again in 5 seconds", StringComparison.Ordinal)));

        // Only one loop is waiting: moving the clock past its pause makes exactly one more pass.
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ParkedAsync(4);
        Assert.Equal(4, ManifestCalls);
        Assert.True(Said("look again in 10 seconds"));
    }
}
