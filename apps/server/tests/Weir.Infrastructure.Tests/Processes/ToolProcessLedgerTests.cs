using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Processes;

/// <summary>The processor time of the tools the runner starts, kept for the System view.</summary>
public sealed class ToolProcessLedgerTests
{
    private static string StandInPath { get; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Weir.TestChild.exe" : "Weir.TestChild");

    [Fact]
    public void A_ledger_with_nothing_tracked_has_no_tools_and_no_time()
    {
        var ledger = new ToolProcessLedger();

        Assert.Equal(0, ledger.Running);
        Assert.Equal(TimeSpan.Zero, ledger.TotalProcessorTime);
    }

    [Fact]
    public async Task A_tool_is_counted_while_it_runs_and_its_time_is_kept_after_it_ends()
    {
        var ledger = new ToolProcessLedger();
        var runner = new ProcessRunner(tools: ledger);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync(
            new ProcessRequest { Argv = [StandInPath, "announce-and-hold"], OnStdoutLine = _ => started.TrySetResult() },
            stop.Token);
        await started.Task;

        var runningCount = ledger.Running;
        var timeWhileRunning = ledger.TotalProcessorTime;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(1, runningCount);
        Assert.Equal(0, ledger.Running);
        Assert.True(ledger.TotalProcessorTime >= timeWhileRunning);
    }

    [Fact]
    public async Task A_runner_without_a_ledger_runs_tools_as_before()
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest { Argv = OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", "exit /b 0"] : ["/bin/sh", "-c", "exit 0"] });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void A_lease_given_back_twice_is_given_back_once()
    {
        var ledger = new ToolProcessLedger();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var lease = ledger.Track(process);
        Assert.Equal(1, ledger.Running);

        lease.Dispose();
        var timeAfterFirst = ledger.TotalProcessorTime;
        lease.Dispose();

        Assert.Equal(0, ledger.Running);
        Assert.Equal(timeAfterFirst, ledger.TotalProcessorTime);
    }
}
