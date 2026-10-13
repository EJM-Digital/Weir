using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Weir.Tray.LanAccess;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What the server says on its standard output and error is kept in server-host.log beside tray-host.log, and a server that never
/// answers is described when the tray gives up on it. A server that hangs before its own log exists has no other trace: the tray
/// log said only that nothing answered.
/// </summary>
public sealed class ServerOutputTests : IDisposable
{
    private const string ReadyAfterVariable = "WEIR_STANDIN_READY_AFTER_MS";
    private const string ErrorLineVariable = "WEIR_STANDIN_ERROR_LINE";
    private const int TestTimeoutMs = 60_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly List<ServerHost> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.StopAsync().GetAwaiter().GetResult();
            host.Dispose();
        }

        Environment.SetEnvironmentVariable(ReadyAfterVariable, null);
        Environment.SetEnvironmentVariable(ErrorLineVariable, null);
        _home.Dispose();
    }

    private string ServerLog => Path.Combine(_home.Path, ServerOutputLog.FileName);

    private ServerHost Host(string? readyAfterMs = null, string? errorLine = null)
    {
        Environment.SetEnvironmentVariable(ReadyAfterVariable, readyAfterMs);
        Environment.SetEnvironmentVariable(ErrorLineVariable, errorLine);
        var install = Path.Combine(_home.Path, "install-" + Guid.NewGuid().ToString("n"));
        StandInServers.Install(Path.Combine(install, "server"));
        var host = new ServerHost(_home.Path, install, FreePort(), ListenScope.ThisPcOnly, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _hosts.Add(host);
        return host;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // The pumps run on their own threads, so the file is read until the text is in it, not after a pause.
    private async Task<string> ServerLogContainingAsync(string text)
    {
        using var deadline = new CancellationTokenSource(Patience);
        while (true)
        {
            var current = ReadServerLog();
            if (current.Contains(text, StringComparison.Ordinal) || deadline.IsCancellationRequested)
            {
                return current;
            }

            await Task.Delay(25, CancellationToken.None);
        }
    }

    private string ReadServerLog()
    {
        try
        {
            using var stream = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task What_the_server_prints_is_kept_with_its_process_id()
    {
        var host = Host();
        await host.StartAsync(CancellationToken.None);

        var log = await ServerLogContainingAsync("ready");

        var line = log.Split('\n').FirstOrDefault(l => l.EndsWith(" ready", StringComparison.Ordinal));
        Assert.True(line is not null, $"server-host.log has no line with what the server printed:\n{log}");
        Assert.Contains($"pid {host.Running!.Value.ProcessId} out]", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task What_the_server_writes_to_its_error_output_is_kept_apart_from_what_it_prints()
    {
        var host = Host(errorLine: "stand-in could not open its log");
        await host.StartAsync(CancellationToken.None);

        var log = await ServerLogContainingAsync("stand-in could not open its log");

        var line = log.Split('\n').FirstOrDefault(l => l.EndsWith("stand-in could not open its log", StringComparison.Ordinal));
        Assert.True(line is not null, $"server-host.log has no line with the server's error output:\n{log}");
        Assert.Contains($"pid {host.Running!.Value.ProcessId} err]", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_server_that_never_answers_is_described_in_the_tray_log_when_the_tray_gives_up_on_it()
    {
        // Never listens at all: the tray waits its short limit, then stops it.
        var host = Host(readyAfterMs: "-1");

        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync(CancellationToken.None));

        var trayLog = File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));
        var described = trayLog.Split('\n').FirstOrDefault(l => l.Contains("did not answer", StringComparison.Ordinal) && l.Contains("processor time", StringComparison.Ordinal));
        Assert.True(described is not null, $"tray-host.log does not describe the server that never answered:\n{trayLog}");
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
    public void The_log_is_started_over_rather_than_growing_without_bound()
    {
        using (var log = new ServerOutputLog(_home.Path, maxBytes: 400))
        {
            for (var line = 0; line < 40; line++)
            {
                log.Write($"line {line:00} of what the server printed");
            }
        }

        var current = new FileInfo(ServerLog);
        var previous = new FileInfo(Path.Combine(_home.Path, ServerOutputLog.PreviousFileName));
        Assert.True(previous.Exists, "the log that filled up was not kept as the previous one");
        Assert.True(current.Length < 400 + 100, $"the current log grew to {current.Length} bytes");
        Assert.Contains("line 39", File.ReadAllText(current.FullName), StringComparison.Ordinal);
        Assert.DoesNotContain("line 00", File.ReadAllText(current.FullName), StringComparison.Ordinal);
    }
}
