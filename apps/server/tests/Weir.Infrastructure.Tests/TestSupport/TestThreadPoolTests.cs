namespace Weir.Infrastructure.Tests;

public sealed class TestThreadPoolTests
{
    /// <summary>
    /// Whether the pool grows when tests hold its threads is settled by the number of threads it has ready, so this asks for that
    /// number: asking the pool to do work while other tests hold its threads would measure those tests (the README says why a test
    /// never does).
    /// </summary>
    [Fact]
    public void The_pool_has_a_thread_ready_for_every_test_that_can_be_holding_one()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);

        // A run that asks for the pool's stall report keeps the pool as the machine gives it, to find the tests that hold many threads.
        Assert.True(PoolStarvationSampler.IsAsked || workers >= TestThreadPool.ReadyThreads, $"workers: {workers}");
        Assert.True(PoolStarvationSampler.IsAsked || completionPorts >= TestThreadPool.ReadyThreads, $"completion ports: {completionPorts}");
    }
}
