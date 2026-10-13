using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Weir.Tray.LanAccess;

namespace Weir.Tray.Tests;

/// <summary>
/// A real <see cref="ServerHost"/> over a real stand-in WeirServer.exe, on a fake clock. The stand-in is steered by environment
/// variables (see its Program.cs); the tests decide when each fake delay passes, so a start that is hung, slow, crashing or fine
/// is a matter of what the stand-in and the test do, never of how long anything takes. The only real waiting is for a real
/// process to do what the test is waiting for, which is a condition and not a duration.
/// </summary>
internal sealed class StartRig : IDisposable
{
    private static readonly TimeSpan RealCeiling = TimeSpan.FromSeconds(30);
    private static readonly string[] SteeringVariables =
    [
        "WEIR_STANDIN_EXIT_CODE", "WEIR_STANDIN_SUCCEED_AT_ATTEMPT", "WEIR_STANDIN_NO_STOP_EVENT", "WEIR_STANDIN_PHASE",
        "WEIR_STANDIN_EXPECT_S", "WEIR_STANDIN_READY_FILE", "WEIR_STANDIN_SAY_FILE", "WEIR_STANDIN_READY_AFTER_MS", "WEIR_STANDIN_ERROR_LINE",
    ];

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    internal StartRig(StartPolicy? policy = null, Func<double>? random01 = null)
    {
        Clock = new DelayWatchingTimeProvider();
        var install = Path.Combine(_home.Path, "install");
        Server = Path.Combine(install, "server");
        StandInServers.Install(Server);
        Host = new ServerHost(_home.Path, install, FreePort(), ListenScope.ThisPcOnly, policy, Clock, random01 ?? (() => 0));
    }

    internal DelayWatchingTimeProvider Clock { get; }

    internal ServerHost Host { get; }

    internal string HomePath => _home.Path;

    internal string Server { get; }

    internal string TrayLog => ReadOrEmpty(Path.Combine(_home.Path, Weir.Tray.TrayLog.FileName));

    internal string ServerLog => ReadOrEmpty(Path.Combine(_home.Path, ServerOutputLog.FileName));

    internal string ReadyFile => Path.Combine(_home.Path, "ready.flag");

    /// <summary>The file the stand-in repeats onto its standard output (WEIR_STANDIN_SAY_FILE).</summary>
    internal string SayFile => Path.Combine(_home.Path, "say.txt");

    /// <summary>Every start the stand-in noted in standin-starts.txt, oldest first.</summary>
    internal IReadOnlyList<(int Pid, string Attempt, string Pipe, string Failures)> Starts
    {
        get
        {
            var starts = new List<(int, string, string, string)>();
            foreach (var line in ReadOrEmpty(Path.Combine(_home.Path, "standin-starts.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A line the stand-in is still writing is not a start yet.
                var parts = line.Split(';', 4);
                if (parts.Length == 4 && parts.All(part => part.Contains('=', StringComparison.Ordinal)) &&
                    int.TryParse(parts[0].Split('=', 2)[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                {
                    starts.Add((pid, parts[1].Split('=', 2)[1], parts[2].Split('=', 2)[1], parts[3].Split('=', 2)[1]));
                }
            }

            return starts;
        }
    }

    /// <summary>Sets a variable the stand-in reads; put back when the rig goes.</summary>
    internal static void Steer(string variable, string? value) => Environment.SetEnvironmentVariable(variable, value);

    /// <summary>The delays the code under test parked on, in order, as <see cref="DriveAsync"/> passed them.</summary>
    internal List<TimeSpan> Delays { get; } = [];

    /// <summary>
    /// Runs <paramref name="work"/> to its end on the fake clock: each delay it parks on is passed in full, after
    /// <paramref name="whenParked"/> (given the delay) has had its say, and is noted in <see cref="Delays"/>.
    /// </summary>
    internal async Task DriveAsync(Task work, Func<TimeSpan, Task>? whenParked = null)
    {
        while (true)
        {
            var parked = Clock.DelayWaitingAsync();
            Task first;
            try
            {
                first = await Task.WhenAny(work, parked).WaitAsync(RealCeiling);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Nothing happened for {RealCeiling.TotalSeconds:0} s of real time. The tray log:{Environment.NewLine}{TrayLog}{Environment.NewLine}The stand-ins' starts: {string.Join(", ", Starts)}");
            }

            if (first == work)
            {
                await work;
                return;
            }

            if (!Clock.TryTakeDelay(out var delay))
            {
                continue;
            }

            Delays.Add(delay);
            if (whenParked is not null)
            {
                await whenParked(delay);
            }

            Clock.Advance(delay);
        }
    }

    /// <summary>Waits, in real time, for something a real process has to do, for no longer than a ceiling that only a broken test reaches.</summary>
    internal static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var ceiling = new CancellationTokenSource(RealCeiling);
        while (!condition())
        {
            if (ceiling.IsCancellationRequested)
            {
                throw new TimeoutException($"Gave up waiting for {what}.");
            }

            await Task.Delay(10, CancellationToken.None);
        }
    }

    /// <summary>Writes the progress a server with this process id would, as the real one does: whole, then renamed into place.</summary>
    internal void WriteProgress(int pid, string phase, int step = 0, int steps = 0, int expectWithinSeconds = 0)
    {
        var json = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"pid\":{pid},\"phase\":\"{phase}\",\"step\":{step},\"steps\":{steps},\"expect_within_s\":{expectWithinSeconds},\"at\":\"{DateTimeOffset.UtcNow:O}\"}}");
        AtomicFile.WriteAllText(_home.Path, StartProgressFile.FileName, json);
    }

    /// <summary>Has the stand-in with this process id print a progress line on its standard output, as the real server does.</summary>
    internal void Say(int pid, string phase, int step = 0, int steps = 0, int expectWithinSeconds = 0) =>
        File.AppendAllText(SayFile, string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{StartProgressFile.Prefix}{{\"pid\":{pid},\"phase\":\"{phase}\",\"step\":{step},\"steps\":{steps},\"expect_within_s\":{expectWithinSeconds},\"at\":\"{DateTimeOffset.UtcNow:O}\"}}{Environment.NewLine}"));

    /// <summary>The process ids of the stand-ins this rig has started that are still running.</summary>
    /// <summary>Waits until the newest stand-in has exited.</summary>
    internal Task LatestExitedAsync() =>
        UntilAsync(() => Starts.Count > 0 && !IsRunning(Starts[^1].Pid), "the newest stand-in server to exit");

    /// <summary>Waits until the newest stand-in is listening on the server port.</summary>
    internal Task LatestListeningAsync() =>
        UntilAsync(() => Starts.Count > 0 && IsRunning(Starts[^1].Pid) && CanConnect(Host.Port), "the newest stand-in server to listen");

    /// <summary>Waits until the newest stand-in has written its first progress.</summary>
    internal Task LatestReportedAsync() =>
        UntilAsync(() => Starts.Count > 0 && StartProgressFile.Read(_home.Path)?.ProcessId == Starts[^1].Pid, "the newest stand-in server to report");

    /// <summary>Waits until the newest stand-in is running.</summary>
    internal Task NewestRunningAsync() =>
        UntilAsync(() => Starts.Count > 0 && IsRunning(Starts[^1].Pid), "the newest stand-in server to be running");

    /// <summary>Starts the server and returns once it is ready, passing the fake delays as the stand-in comes up.</summary>
    internal async Task StartReadyAsync()
    {
        var start = Host.StartAsync(CancellationToken.None);
        await DriveAsync(start, async delay =>
        {
            if (delay == ServerHealth.RetryDelay)
            {
                await LatestListeningAsync();
            }
        });
    }

    /// <summary>Waits until the newest stand-in has started at all.</summary>
    internal Task LatestStartedAsync(int count) =>
        UntilAsync(() => Starts.Count >= count, "the stand-in server to start");

    internal IReadOnlyList<int> Running() => [.. Starts.Select(start => start.Pid).Where(IsRunning)];

    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // The steering goes first: a clean-up that fails must not leave the next test's stand-ins steered by this one's.
        foreach (var variable in SteeringVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        try
        {
            Host.StopAsync().GetAwaiter().GetResult();
            Host.Dispose();
            foreach (var pid in Running())
            {
                Kill(pid);
            }
        }
        finally
        {
            _home.Dispose();
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is what this clean-up wants.
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
