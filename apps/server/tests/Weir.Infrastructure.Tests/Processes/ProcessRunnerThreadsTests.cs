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

    /// <summary>
    /// Starting a tool is a call that waits for the operating system, and it waits longest for a program run for the first time: on a
    /// fresh Windows install the virus scanner holds the first start of ffprobe, mkvmerge or ffmpeg while it reads the file. The
    /// first films handed over start several at once, so on a pool that begins with as many threads as there are processors the
    /// starts held every one and the next request waited, as the intake of 28 October 2026 did.
    /// </summary>
    [Fact]
    public async Task A_tool_is_started_on_a_thread_of_its_own_not_one_of_the_pools()
    {
        bool? startedOnPool = null;
        var runner = new ProcessRunner();
        string[] saysNothing = OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", "exit 0"] : ["/bin/sh", "-c", "exit 0"];

        // Asked from a pool thread, as every job and request is.
        var result = await Task.Run(() => runner.RunAsync(new ProcessRequest
        {
            Argv = saysNothing,
            OnStarted = _ => startedOnPool = Thread.CurrentThread.IsThreadPoolThread,
        }));

        Assert.Equal(0, result.ExitCode);
        Assert.False(startedOnPool);
    }

    [Fact]
    public async Task A_tool_that_is_not_there_is_still_refused_with_the_error_of_the_system()
    {
        var missing = Path.Join(Path.GetTempPath(), "weir-no-such-tool-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => new ProcessRunner().RunAsync(new ProcessRequest { Argv = [missing] }));
    }
}
