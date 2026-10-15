using System.Runtime.CompilerServices;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Gives the test process thread-pool threads at once instead of one or two a second. Everything the harness hears from a server
/// arrives on the pool: its output, the answers of the fakes it talks to, and every request a test makes. With the default of one thread
/// per processor, a burst of work that waits on files and processes (a dozen servers starting side by side on a busy machine) leaves
/// those callbacks queued behind it for minutes, so a server that is up seems not to be and a fake seems not to answer.
/// </summary>
internal static class TestProcessThreads
{
    internal const int MinimumThreads = 256;

    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, MinimumThreads), Math.Max(completionPorts, MinimumThreads));
    }
}
