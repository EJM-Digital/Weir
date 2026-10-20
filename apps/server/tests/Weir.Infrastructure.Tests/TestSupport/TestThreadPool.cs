using System.Runtime.CompilerServices;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// Gives the test process's thread pool as many threads ready as the tests can hold at once. Every test class runs at the same time
/// on the process's one pool, and a test that waits on a pool thread (a child process, a file watcher, a blocking read) keeps that
/// thread. The pool starts with a thread per processor, four on a CI runner, and once they are all held it adds one about every half
/// second, so the next piece of work from any test, a timer's callback or the end of an await included, waits for that growth. On
/// four processors that wait reaches six seconds, and a test that waits for something to happen inside a limit fails when its
/// callback is one of those waiting. With the threads ready, work starts at once however many tests are holding threads. The
/// threads are only made when work needs them.
/// </summary>
/// <remarks>
/// <see cref="PoolStarvationSampler"/> exists to find the tests that hold many threads, which this would hide, so a run that asks for
/// its report keeps the pool as the machine gives it.
/// </remarks>
internal static class TestThreadPool
{
    /// <summary>Far more than the tests ever hold at once: xUnit runs no more classes at the same time than there are processors.</summary>
    internal const int ReadyThreads = 128;

    // The attribute is how a test assembly runs code once at load.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        if (PoolStarvationSampler.IsAsked)
        {
            return;
        }

        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, ReadyThreads), Math.Max(completionPorts, ReadyThreads));
    }
}
