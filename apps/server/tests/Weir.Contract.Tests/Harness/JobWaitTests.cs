using System.Text.Json.Nodes;
using Weir.Contract.Tests.Jobs;
using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// A scan or a clean takes as long as the machine makes it take, so a test waits for one for as long as Weir is working on it and
/// gives up only on work nobody took up. The server is real and the clock is the test's own, so a clean that has been running for an hour
/// is one the test does not have to run for an hour.
/// </summary>
[ContractArea("harness")]
public sealed class JobWaitTests
{
    private const string PauseUrl = $"{WeirClient.Api}/pause";

    [Fact]
    public async Task A_clean_a_worker_still_has_is_waited_for_however_long_it_has_been_running()
    {
        // The swap stops at its first point and stays there, so the clean is a job a worker holds that does not end.
        await using var world = await SwapWorld.StartAsync(keepOriginal: false, holdAt: "Started");
        await world.RequestCleanAsync();
        await world.HeldAtAsync();
        var clock = new SteppedClock();
        var polls = new Polls();
        var finished = false;
        var wait = new JobWait(clock, polls.Counted).UntilAsync(
            async () => [(string)(await world.CleanJobAsync())["status"]!],
            () => Task.FromResult(Volatile.Read(ref finished) ? new JsonObject() : null),
            "the clean");
        await polls.MoreAsync(2);

        clock.Advance(JobWait.UnclaimedCeiling * 30);
        await polls.MoreAsync(2);

        Assert.False(wait.IsCompleted, "a clean a worker is running must be waited for, not given up on");
        Volatile.Write(ref finished, true);
        await wait;
    }

    [Fact]
    public async Task A_clean_no_worker_takes_up_fails_the_wait_once_the_ceiling_has_passed()
    {
        await using var world = await SwapWorld.StartAsync(keepOriginal: false);
        var paused = await world.Client.PutWithCsrfAsync(PauseUrl, new JsonObject { ["paused"] = true, ["scan_while_paused"] = false });
        Assert.Equal(System.Net.HttpStatusCode.OK, paused.Status);
        await world.RequestCleanAsync();
        var clock = new SteppedClock();
        var polls = new Polls();
        var wait = new JobWait(clock, polls.Counted).UntilAsync(
            async () => [(string)(await world.CleanJobAsync())["status"]!],
            () => Task.FromResult<JsonObject?>(null),
            "the clean");
        await polls.MoreAsync(2);
        Assert.False(wait.IsCompleted);

        clock.Advance(JobWait.UnclaimedCeiling);

        var failure = await Assert.ThrowsAsync<XunitException>(() => wait);
        Assert.StartsWith("Nothing was working on the job for 120s while waiting for the clean; last saw: jobs pending, no result yet", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_that_ended_without_the_result_fail_the_wait_at_once()
    {
        var failure = await Assert.ThrowsAsync<XunitException>(() => new JobWait().UntilAsync(
            () => Task.FromResult<IReadOnlyList<string>>(["completed"]),
            () => Task.FromResult<JsonObject?>(null),
            "the clean of Film.mkv"));

        Assert.Equal("Weir's jobs ended (completed) but the clean of Film.mkv never appeared.", failure.Message);
    }

    /// <summary>Counts the polls a wait has made, so a test moves its clock between polls instead of sleeping.</summary>
    private sealed class Polls
    {
        private readonly Lock _gate = new();
        private readonly List<(int Target, TaskCompletionSource Reached)> _waiting = [];
        private int _made;

        public void Counted()
        {
            lock (_gate)
            {
                _made++;
                foreach (var waiting in _waiting.Where(waiting => waiting.Target <= _made))
                {
                    waiting.Reached.TrySetResult();
                }
            }
        }

        /// <summary>Completes after <paramref name="count"/> more polls; with two, one is certainly begun after the call.</summary>
        public Task MoreAsync(int count)
        {
            lock (_gate)
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Add((_made + count, reached));
                return reached.Task;
            }
        }
    }

    private sealed class SteppedClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }
    }
}
