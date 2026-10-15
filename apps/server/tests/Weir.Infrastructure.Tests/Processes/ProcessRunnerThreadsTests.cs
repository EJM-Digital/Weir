using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Processes;

/// <summary>
/// A running tool must not cost the server thread-pool threads. On Windows the wait for a redirected pipe holds its thread, so a tool
/// whose output was read through the pool would hold two of them for as long as it ran, and a machine with four processors would
/// have none ready for requests, timers or the next job once two tools were running.
/// </summary>
public sealed class ProcessRunnerThreadsTests
{
    [Fact]
    public async Task A_tools_output_is_read_on_a_thread_of_its_own()
    {
        var onPool = new List<bool>();
        string[] saysTwoLines = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/c", "echo one& echo two"]
            : ["/bin/sh", "-c", "echo one; echo two"];

        var result = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            Argv = saysTwoLines,
            OnStdoutLine = _ =>
            {
                lock (onPool)
                {
                    onPool.Add(Thread.CurrentThread.IsThreadPoolThread);
                }
            },
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, onPool.Count);
        Assert.DoesNotContain(true, onPool);
    }
}
