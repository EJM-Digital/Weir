namespace Weir.Contract.Tests.Harness;

/// <summary>The pool must take new work while many of its threads are busy waiting, as they are when many servers start at once.</summary>
[ContractArea("harness")]
public sealed class TestProcessThreadsTests
{
    private const int BusyThreads = 64;

    [Fact]
    public async Task Work_queued_behind_many_waiting_threads_runs_without_waiting_for_the_pool_to_grow()
    {
        using var release = new ManualResetEventSlim();
        using var allWaiting = new CountdownEvent(BusyThreads);
        for (var thread = 0; thread < BusyThreads; thread++)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                allWaiting.Signal();
                release.Wait();
            });
        }

        try
        {
            // The pool adds a thread about every half second beyond its minimum, so 64 waiting threads would take it close to a minute
            // to make room for this one.
            var ran = Task.Run(() => allWaiting.Wait());
            var finished = await Task.WhenAny(ran, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(ran, finished);
        }
        finally
        {
            release.Set();
        }
    }
}
