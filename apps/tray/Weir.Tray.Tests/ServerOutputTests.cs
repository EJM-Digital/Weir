using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What the server says on its standard output and error is kept in server-host.log beside tray-host.log, and a server that never
/// answers is described when the tray gives up on it. A server that hangs before its own log exists has no other trace: the tray
/// log said only that nothing answered.
/// </summary>
public sealed class ServerOutputTests
{
    private const int TestTimeoutMs = 60_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // The pumps run on their own threads, so the file is read until the text is in it, not after a pause.
    private static async Task<string> ServerLogContainingAsync(StartRig rig, string text)
    {
        using var deadline = new CancellationTokenSource(Patience);
        while (true)
        {
            var current = rig.ServerLog;
            if (current.Contains(text, StringComparison.Ordinal) || deadline.IsCancellationRequested)
            {
                return current;
            }

            await Task.Delay(25, CancellationToken.None);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task What_the_server_prints_is_kept_with_its_process_id()
    {
        using var rig = new StartRig();
        await rig.StartReadyAsync();

        var log = await ServerLogContainingAsync(rig, "ready");

        var line = log.Split('\n').FirstOrDefault(l => l.EndsWith(" ready", StringComparison.Ordinal));
        Assert.True(line is not null, $"server-host.log has no line with what the server printed:\n{log}");
        Assert.Contains($"pid {rig.Host.Running!.Value.ProcessId} out]", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task What_the_server_writes_to_its_error_output_is_kept_apart_from_what_it_prints()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_ERROR_LINE", "stand-in could not open its log");
        await rig.StartReadyAsync();

        var log = await ServerLogContainingAsync(rig, "stand-in could not open its log");

        var line = log.Split('\n').FirstOrDefault(l => l.EndsWith("stand-in could not open its log", StringComparison.Ordinal));
        Assert.True(line is not null, $"server-host.log has no line with the server's error output:\n{log}");
        Assert.Contains($"pid {rig.Host.Running!.Value.ProcessId} err]", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_never_answers_is_described_in_the_tray_log_when_the_tray_gives_up_on_it()
    {
        using var rig = new StartRig();
        // Alive and answering "not ready", but silent: hung before it said anything.
        StartRig.Steer("WEIR_STANDIN_NO_STOP_EVENT", "1");
        StartRig.Steer("WEIR_STANDIN_READY_FILE", rig.ReadyFile);

        var start = rig.Host.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<StartGaveUpException>(() => rig.DriveAsync(start, async delay =>
        {
            if (delay == ServerHealth.RetryDelay)
            {
                await rig.LatestListeningAsync();
            }
        }));

        var described = rig.TrayLog.Split('\n').FirstOrDefault(l => l.Contains("did not answer", StringComparison.Ordinal) && l.Contains("processor time", StringComparison.Ordinal));
        Assert.True(described is not null, $"tray-host.log does not describe the server that never answered:\n{rig.TrayLog}");
        Assert.Contains("threads", described, StringComparison.Ordinal);
        Assert.Contains("WeirServer.exe", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_process_is_described_by_its_processor_time_threads_and_loaded_files()
    {
        using var self = Process.GetCurrentProcess();

        var text = ServerProcessSnapshot.Describe(self);

        Assert.Contains("processor time", text, StringComparison.Ordinal);
        Assert.Contains("threads", text, StringComparison.Ordinal);
        Assert.Contains("loaded", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_that_has_gone_is_said_to_have_exited_not_described_from_nothing()
    {
        var started = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        started.WaitForExit();

        var text = ServerProcessSnapshot.Describe(started);

        Assert.Contains("had already exited", text, StringComparison.Ordinal);
        Assert.DoesNotContain("processor time", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_that_was_never_started_is_said_to_be_beyond_looking_at()
    {
        using var never = new Process();

        var text = ServerProcessSnapshot.Describe(never);

        Assert.StartsWith("could not be looked at (", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_that_has_gone_has_no_modules_to_list()
    {
        var started = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        started.WaitForExit();

        Assert.Empty(ServerProcessSnapshot.LoadedFiles(started));
    }

    [Fact]
    public void A_process_that_has_loaded_only_windows_files_is_described_without_a_list_of_more()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c pause") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        using var holder = Process.Start(start)!;
        try
        {
            var text = ServerProcessSnapshot.Describe(holder);

            Assert.Contains("had loaded 0 files outside Windows: ", text, StringComparison.Ordinal);
            Assert.DoesNotContain(" more", text, StringComparison.Ordinal);
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void The_log_is_started_over_rather_than_growing_without_bound()
    {
        using var home = TempDirectory.Create();
        using (var log = new ServerOutputLog(home.Path, maxBytes: 400))
        {
            for (var line = 0; line < 40; line++)
            {
                log.Write($"line {line:00} of what the server printed");
            }
        }

        var current = new FileInfo(Path.Combine(home.Path, ServerOutputLog.FileName));
        var previous = new FileInfo(Path.Combine(home.Path, ServerOutputLog.PreviousFileName));
        Assert.True(previous.Exists, "the log that filled up was not kept as the previous one");
        Assert.True(current.Length < 400 + 100, $"the current log grew to {current.Length} bytes");
        Assert.Contains("line 39", File.ReadAllText(current.FullName), StringComparison.Ordinal);
        Assert.DoesNotContain("line 00", File.ReadAllText(current.FullName), StringComparison.Ordinal);
    }
}
