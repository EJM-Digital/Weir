using System.Diagnostics;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Following a server's output must not cost the test process thread-pool threads. On Windows a read of a redirected pipe holds
/// the thread that waits for it until the process ends, so a log that read through the pool would take two threads for every
/// server that is running. A suite that has more servers up than the pool has threads ready then stops answering: the fake
/// managers cannot reply, the wait for a server to be ready cannot look, and every test waits for the pool to add threads, which
/// it does about two a second.
/// </summary>
[ContractArea("harness")]
public sealed class ServerLogTests : IDisposable
{
    private const string Greeting = "up";

    // Long enough for a pool that has threads ready to carry the output over, and far short of the time a starved one takes to grow.
    private static readonly TimeSpan PoolMustAnswerWithin = TimeSpan.FromSeconds(20);

    private readonly TemporaryFolder _folder = new();
    private readonly List<Process> _started = [];
    private readonly List<ServerLog> _logs = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }

        foreach (var log in _logs)
        {
            log.Dispose();
        }

        _folder.Dispose();
    }

    [Fact]
    public async Task Following_more_servers_than_the_pool_has_threads_leaves_the_pool_free()
    {
        ThreadPool.GetMinThreads(out var ready, out _);
        var servers = ready + 8;
        for (var index = 0; index < servers; index++)
        {
            Follow(StartWaitingProcess());
        }

        await Poll.UntilAsync(
            () => Task.FromResult(_logs.All(log => log.Text().Contains(Greeting, StringComparison.Ordinal))),
            "the output of every server to reach its log",
            PoolMustAnswerWithin);
    }

    [Fact]
    public async Task The_output_of_a_process_that_has_ended_is_all_in_the_log_once_its_streams_have_ended()
    {
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/c \"echo said& echo complained 1>&2\"") : new ProcessStartInfo("sh");
        if (!OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("echo said; echo complained 1>&2");
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        _started.Add(process);
        var log = Follow(process);

        await process.WaitForExitAsync();
        await log.OutputEnded.WaitAsync(PoolMustAnswerWithin);

        var lines = log.Text().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Order().ToArray();
        Assert.Equal(["complained", "said"], lines);
    }

    private Process StartWaitingProcess()
    {
        // Says one line, then waits for input that never comes, so it stays up until it is stopped.
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", $"/c \"echo {Greeting}& pause\"") : new ProcessStartInfo("sh");
        if (!OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"echo {Greeting}; cat");
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("The waiting process did not start.");
        _started.Add(process);
        return process;
    }

    private ServerLog Follow(Process process)
    {
        var log = new ServerLog(Path.Combine(_folder.Path, $"server-{_logs.Count}.log"));
        _logs.Add(log);
        log.Follow(process);
        return log;
    }
}
