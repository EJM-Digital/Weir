using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>
/// The bundled server process: starting it, waiting for it to be ready, supervising every start of it (ServerHost.Supervision.cs),
/// moving it to another port or to another set of devices that may connect, and stopping it. Nothing here touches
/// the UI; the tray is told about outcomes and shows them itself.
/// </summary>
sealed partial class ServerHost : IServerListenScope, IDisposable
{
    /// <summary>The port the server listens on at the moment. The saved choice is port.txt (PortChoice).</summary>
    internal const string CurrentPortFileName = "current-port.txt";

    private const string ServerExeName = "WeirServer.exe";
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long a stop waits for a restart or port change in progress to let go of the server.</summary>
    private static readonly TimeSpan StopGateWait = TimeSpan.FromSeconds(5);

    private readonly string _runtimeHome;
    private readonly string _installRoot;
    private readonly HttpClient _http = new() { Timeout = ServerHealth.RequestTimeout };
    private readonly ServerOutputLog _output;

    // Held while the server is started, replaced or stopped, by the watchdog, a port change or a quit, so no two of
    // them act on the process at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _processGate = new();
    private volatile Process? _process;
    private volatile int _port;
    private volatile ListenScope _scope;
    private volatile ServerPhase _phase = ServerPhase.Starting;
    private volatile StartupError? _startupError;
    private long _startedAtTicks;
    private long _startedAtStamp;
    private volatile StartProgressFeed? _feed;
    private volatile int _gaveUpAttempts;
    private Task? _watchdog;
    private Action? _onGaveUp;
    private CancellationToken _watchdogToken;

    private readonly StartPolicy _policy;
    private readonly TimeProvider _time;
    private readonly StartSupervision _supervision;

    // The policy says how every start is supervised (StartPolicy); tests give a short one, a fake clock and a fixed jitter.
    internal ServerHost(
        string runtimeHome,
        string installRoot,
        int port,
        ListenScope scope,
        StartPolicy? policy = null,
        TimeProvider? time = null,
        Func<double>? random01 = null)
    {
        _runtimeHome = runtimeHome;
        _installRoot = installRoot;
        _port = port;
        _scope = scope;
        _output = new ServerOutputLog(runtimeHome);
        _policy = policy ?? StartPolicy.Default;
        _time = time ?? TimeProvider.System;
        _supervision = new StartSupervision(_policy, _time, random01);
    }

    /// <summary>Whether the server process this host started is still running, ready or not.</summary>
    internal bool ServerIsRunning => _process is { HasExited: false };

    /// <summary>Raised, on whichever thread changed it, when <see cref="Phase"/> changes.</summary>
    internal event Action? PhaseChanged;

    internal int Port => _port;

    /// <summary>The server process this host started, while it runs, or null.</summary>
    internal RunningServer? Running =>
        _process is { HasExited: false } process && StartedAtUtc is { } started ? new RunningServer(process.Id, started) : null;

    /// <summary>Where the server is: being started, answering, or stopped with nothing about to bring it back.</summary>
    internal ServerPhase Phase => _phase;

    public ListenScope Scope => _scope;

    /// <summary>When the server process this host last started began, or null if it has started none.</summary>
    internal DateTime? StartedAtUtc => Volatile.Read(ref _startedAtTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>
    /// Why the server could not start, in its own words, while <see cref="Phase"/> is Stopped and the server said why
    /// (StartupNotes); null otherwise.
    /// </summary>
    internal StartupError? StartupError => _startupError;

    /// <summary>
    /// How many attempts the supervision made before it gave up, while <see cref="Phase"/> is Stopped for that reason; 0 otherwise.
    /// </summary>
    internal int GaveUpAfterAttempts => _gaveUpAttempts;

    /// <summary>The hover text's reason for a server the supervision gave up on, when it did and the server itself said nothing.</summary>
    internal StartupError? GaveUpError => _gaveUpAttempts > 0 ? new StartupError($"Didn't start after {_gaveUpAttempts} tries", string.Empty) : null;

    private void SetPhase(ServerPhase phase)
    {
        if (_phase == phase)
        {
            return;
        }
        _startupError = phase == ServerPhase.Stopped ? StartupNotes.ReadError(_runtimeHome, StartedAtUtc) : null;
        _phase = phase;
        PhaseChanged?.Invoke();
    }

    /// <summary>Sets what every server process is started with (ServerEnvironment).</summary>
    internal void PrepareEnvironment() => ServerEnvironment.Apply(_runtimeHome, FindServerExeDirectory());

    internal void WritePortFile() =>
        File.WriteAllText(Path.Combine(_runtimeHome, CurrentPortFileName), _port.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Restarts the server on <paramref name="to"/> and saves it as the chosen port. If it does not come up there,
    /// goes back to the port it had, which was working a moment ago and is still the saved one. Returns whether
    /// the move succeeded.
    /// </summary>
    internal async Task<bool> MoveToPortAsync(int to, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var from = _port;
        try
        {
            TrayLog.Write($"Change port: restarting the server on port {to} (was {from}).");
            if (await TryStartOnAsync(to, _scope, cancellationToken).ConfigureAwait(false))
            {
                PortChoice.Save(_runtimeHome, to);
                WritePortFile();
                TrayLog.Write($"Change port: Weir is healthy on http://127.0.0.1:{to}/ and port {to} is saved.");
                return true;
            }
            TrayLog.Write($"Change port: going back to port {from}.");
            if (await TryStartOnAsync(from, _scope, cancellationToken).ConfigureAwait(false))
            {
                WritePortFile();
            }
            else
            {
                SetPhase(ServerPhase.Stopped);
            }
            return false;
        }
        finally
        {
            _gate.Release();
            RestartWatchdogIfStopped();
        }
    }

    /// <summary>
    /// Restarts the server for <paramref name="to"/> on the same port. If it does not come up, goes back to the
    /// scope it had. Saving the choice is the caller's job (LanAccessSync).
    /// </summary>
    public async Task<ScopeChange> MoveToScopeAsync(ListenScope to, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var from = _scope;
        try
        {
            if (from == to)
            {
                return ScopeChange.Unchanged;
            }
            TrayLog.Write($"LAN access: restarting the server so that {to.Describe()} (it was: {from.Describe()}).");
            if (await TryStartOnAsync(_port, to, cancellationToken).ConfigureAwait(false))
            {
                return ScopeChange.Applied;
            }
            TrayLog.Write($"LAN access: the server did not start that way, so it goes back to how it was: {from.Describe()}.");
            if (!await TryStartOnAsync(_port, from, cancellationToken).ConfigureAwait(false))
            {
                SetPhase(ServerPhase.Stopped);
            }
            return ScopeChange.Failed;
        }
        finally
        {
            _gate.Release();
            RestartWatchdogIfStopped();
        }
    }

    /// <summary>Stops the server, waiting briefly for a restart or port change in progress to let go of it first.</summary>
    internal async Task StopAsync()
    {
        var entered = await _gate.WaitAsync(StopGateWait).ConfigureAwait(false);
        try
        {
            // The icon says so while the server finishes its jobs, which can take most of a minute.
            SetPhase(ServerPhase.Stopping);
            await StopProcessAsync().ConfigureAwait(false);
            SetPhase(ServerPhase.Stopped);
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private async Task StopProcessAsync()
    {
        var process = TakeProcess();
        if (process is null)
        {
            return;
        }

        using (process)
        {
            await ServerProcessStop.StopAsync(process).ConfigureAwait(false);
        }
    }

    // Takes the server process out of _process, so that whoever has it is the one to stop and dispose it: the process of the attempt
    // under way (`only`), or whichever is there. False/null when it is already gone or someone else has it.
    private Process? TakeProcess()
    {
        lock (_processGate)
        {
            var process = _process;
            _process = null;
            return process;
        }
    }

    private bool TakeProcess(Process only)
    {
        lock (_processGate)
        {
            if (!ReferenceEquals(_process, only))
            {
                return false;
            }

            _process = null;
            return true;
        }
    }

    // A server that has already exited cannot say when it started; it started no later than now.
    private static DateTime StartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return DateTime.UtcNow;
        }
    }

    private string FindServerExeDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(_installRoot, "server", ServerExeName),
            Path.Combine(_installRoot, ServerExeName),
            Path.Combine(_installRoot, "..", ServerExeName),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(Path.GetFullPath(candidate))!;
            }
        }
        return _installRoot;
    }

    private string FindServerExe()
    {
        var exe = Path.Combine(FindServerExeDirectory(), ServerExeName);
        return File.Exists(exe) ? exe : throw new FileNotFoundException("Bundled server host is missing.", exe);
    }

    // Stopping the server is always explicit (quit, update, port change). A start-up whose health wait timed out
    // leaves the server running, and the next tray start stops it as an orphan (Program.StopOrphanedServers).
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        _output.Dispose();
    }
}
