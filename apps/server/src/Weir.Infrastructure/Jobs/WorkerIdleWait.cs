using Weir.Core.Workers;

namespace Weir.Infrastructure.Jobs;

/// <summary>How an idle worker slot waits, shared by every worker lane.</summary>
internal static class WorkerIdleWait
{
    /// <summary>
    /// Beats the slot's heartbeat once, then waits out <see cref="WorkerLoopTimings.IdleSleep"/> or less when a job is
    /// queued. One heartbeat covers the wait, which is far shorter than <see cref="WorkerHeartbeats.StaleAfter"/>. A test on a fake
    /// clock passes <c>waiting</c>, called once the wait has started, to know when to wake the slot or move time.
    /// </summary>
    public static async Task RunAsync(
        string heartbeatModule,
        int workerIndex,
        WorkerHeartbeats heartbeats,
        Task wake,
        WorkerLoopTimings timings,
        TimeProvider time,
        CancellationToken stoppingToken,
        Action? waiting = null)
    {
        heartbeats.Beat(heartbeatModule, workerIndex);
        await WaitForWorkAsync(wake, timings.IdleSleep, time, stoppingToken, waiting).ConfigureAwait(false);
    }

    /// <summary>Returns when <paramref name="wake"/> completes or <paramref name="idle"/> has passed, whichever is first.</summary>
    internal static async Task WaitForWorkAsync(Task wake, TimeSpan idle, TimeProvider time, CancellationToken stoppingToken, Action? waiting = null)
    {
        using var idleTimer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var idleOver = Task.Delay(idle, time, idleTimer.Token);
        waiting?.Invoke();
        if (await Task.WhenAny(wake, idleOver).ConfigureAwait(false) == idleOver)
        {
            // Throws when the host is stopping, which ends the slot as before.
            await idleOver.ConfigureAwait(false);
            return;
        }

        await idleTimer.CancelAsync().ConfigureAwait(false);
    }
}
