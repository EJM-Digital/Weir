using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>Long blocking calls run on threads of their own, so the thread pool stays free for requests and the live stream.</summary>
public sealed class BlockingWorkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_work_runs_on_a_thread_of_its_own()
    {
        Assert.False(await BlockingWork.RunAsync(() => Thread.CurrentThread.IsThreadPoolThread));
    }

    [Fact]
    public async Task A_result_and_a_failure_come_back_to_the_caller()
    {
        Assert.Equal(7, await BlockingWork.RunAsync(() => 7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BlockingWork.RunAsync(() => throw new InvalidOperationException("copy failed")));
    }

    /// <summary>
    /// Asked of each call itself, never of the thread pool's speed, which depends on every other test running at the same time: a
    /// call that took a pool thread would be seen to, and the pool is not what limits how many can be in flight at once.
    /// </summary>
    [Fact]
    public async Task Many_blocking_calls_in_flight_at_once_take_no_thread_from_the_pool()
    {
        // More than the pool's minimum, which is what turns blocked pool threads into seconds of delay for everything else.
        var blocked = Environment.ProcessorCount * 4 + 8;
        using var release = new ManualResetEventSlim();
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var calls = Enumerable.Range(0, blocked)
            .Select(_ => BlockingWork.RunAsync(() =>
            {
                var onThePool = Thread.CurrentThread.IsThreadPoolThread;
                if (Interlocked.Increment(ref running) == blocked)
                {
                    allRunning.SetResult();
                }

                release.Wait(TimeSpan.FromSeconds(60));
                return onThePool;
            }))
            .ToArray();
        try
        {
            await allRunning.Task.WaitAsync(Limit);
        }
        finally
        {
            release.Set();
        }

        Assert.All(await Task.WhenAll(calls).WaitAsync(Limit), onThePool => Assert.False(onThePool));
    }
}
