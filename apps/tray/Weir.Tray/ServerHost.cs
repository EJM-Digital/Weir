using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>
/// The bundled server process: starting it, waiting for it to be ready, restarting it when it exits unexpectedly,
/// moving it to another port or to another set of devices that may connect, and stopping it. Nothing here touches
/// the UI; the tray is told about outcomes and shows them itself.
/// </summary>
sealed class ServerHost : IServerListenScope, IDisposable
{
    /// <summary>The port the server listens on at the moment. The saved choice is port.txt (PortChoice).</summary>
    internal const string CurrentPortFileName = "current-port.txt";

    private const string ServerExeName = "WeirServer.exe";
    private const int MaxRestarts = 5;

    /// <summary>
    /// How long a server that has not answered at all is waited for: a server that cannot listen, or a port something else holds,
    /// shows within a minute. One that exits is noticed at once.
    /// </summary>
    internal static readonly TimeSpan DefaultNothingAnswersTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a server that has answered "not ready" is waited for. It is alive and starting; a slow start, such as a large
    /// database migrated on a busy PC, must not be reported as a failure while it is still going to succeed.
    /// </summary>
    internal static readonly TimeSpan DefaultStartingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long a stop waits for a restart or port change in progress to let go of the server.</summary>
    private static readonly TimeSpan StopGateWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly string _runtimeHome;
    private readonly string _installRoot;
    private readonly HttpClient _http = new() { Timeout = ServerHealth.RequestTimeout };
    private readonly ServerOutputLog _output;

    // Held while the server is started, replaced or stopped, by the watchdog, a port change or a quit, so no two of
    // them act on the process at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile Process? _process;
    private volatile int _port;
    private volatile ListenScope _scope;
    private volatile ServerPhase _phase = ServerPhase.Starting;
    private volatile StartupError? _startupError;
    private long _startedAtTicks;
    private bool _waitingForSave;
    private Task? _watchdog;
    private Action? _onGaveUp;
    private CancellationToken _watchdogToken;

    private readonly TimeSpan _nothingAnswersTimeout;
    private readonly TimeSpan _startingTimeout;

    // The two timeouts are how long to wait for a server that has not answered at all and for one that has answered "not ready"; the
    // defaults when null. Tests shorten them.
    internal ServerHost(
        string runtimeHome,
        string installRoot,
        int port,
        ListenScope scope,
        TimeSpan? nothingAnswersTimeout = null,
        TimeSpan? startingTimeout = null)
    {
        _runtimeHome = runtimeHome;
        _installRoot = installRoot;
        _port = port;
        _scope = scope;
        _output = new ServerOutputLog(runtimeHome);
        _nothingAnswersTimeout = nothingAnswersTimeout ?? DefaultNothingAnswersTimeout;
        _startingTimeout = startingTimeout ?? DefaultStartingTimeout;
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

    /// <summary>Starts the server and returns once it is ready.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StartProcess();
            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            SetPhase(ServerPhase.Running);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            SetPhase(ServerPhase.Stopped);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the server cleanly and starts it again on the same port, for the same devices. This is also the way back once
    /// the watchdog has given up. Returns whether the server is back and ready.
    /// </summary>
    internal async Task<bool> RestartNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TrayLog.Write($"Restart: restarting the server on port {_port} ({_scope.Describe()}).");
            if (await TryStartOnAsync(_port, _scope, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            SetPhase(ServerPhase.Stopped);
            return false;
        }
        finally
        {
            _gate.Release();
            RestartWatchdogIfStopped();
        }
    }

    /// <summary>
    /// Watches the server and restarts it, with growing pauses, when it exits on its own. After
    /// <see cref="MaxRestarts"/> failed restarts in a row it gives up and calls <paramref name="onGaveUp"/>.
    /// </summary>
    internal void Watch(Action onGaveUp, CancellationToken cancellationToken)
    {
        _onGaveUp = onGaveUp;
        _watchdogToken = cancellationToken;
        LaunchWatchdog();
    }

    private void LaunchWatchdog()
    {
        var onGaveUp = _onGaveUp!;
        _watchdog = BackgroundWork.RunLoop("Server watchdog", ct => WatchAsync(onGaveUp, ct), _watchdogToken);
    }

    // A port change that brings the server back restarts a watchdog that stopped: it stops when it sees the process
    // cleared, and when it gives up after repeated failures.
    private void RestartWatchdogIfStopped()
    {
        if (_onGaveUp is null || _watchdogToken.IsCancellationRequested || _watchdog is { IsCompleted: false })
        {
            return;
        }
        LaunchWatchdog();
    }

    private async Task WatchAsync(Action onGaveUp, CancellationToken cancellationToken)
    {
        var failedRestarts = 0;
        using var timer = new PeriodicTimer(WatchInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var (state, exited) = await CheckProcessAsync(cancellationToken).ConfigureAwait(false);
            if (state == ProcessState.Stopped)
            {
                // Stopped on purpose (quit, update).
                return;
            }
            if (exited is null)
            {
                continue;
            }

            TrayLog.Write($"Bundled server host exited unexpectedly with code {exited.ExitCode} (restart {failedRestarts + 1}/{MaxRestarts})");
            if (failedRestarts >= MaxRestarts)
            {
                TrayLog.Write("Exceeded max restart attempts — giving up.");
                SetPhase(ServerPhase.Stopped);
                onGaveUp();
                return;
            }

            SetPhase(ServerPhase.Starting);
            var delay = RestartBackoff[Math.Min(failedRestarts, RestartBackoff.Length - 1)];
            TrayLog.Write($"Waiting {delay.TotalMilliseconds:0}ms before restarting server...");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            failedRestarts = await RestartAsync(exited, cancellationToken).ConfigureAwait(false) ? 0 : failedRestarts + 1;
            if (failedRestarts > 0 && StartupNotes.ReadError(_runtimeHome, StartedAtUtc) is { } reason)
            {
                // The server said why it cannot start. One more try has been made; another would fail the same way.
                TrayLog.Write($"The server said why it cannot start, so it is not tried again: {reason.Detail}");
                SetPhase(ServerPhase.Stopped);
                onGaveUp();
                return;
            }
        }
    }

    private enum ProcessState
    {
        Running,
        Exited,
        Stopped,
    }

    // The process comes back only when it has exited on its own.
    private async Task<(ProcessState State, Process? Exited)> CheckProcessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            return process switch
            {
                null => (ProcessState.Stopped, null),
                { HasExited: true } => (ProcessState.Exited, process),
                _ => (ProcessState.Running, null),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // Returns whether the server is back and healthy.
    private async Task<bool> RestartAsync(Process exited, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A port change may have replaced the server while the watchdog waited.
            if (!ReferenceEquals(_process, exited))
            {
                return true;
            }
            StartProcess();
            exited.Dispose();
            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            SetPhase(ServerPhase.Running);
            TrayLog.Write("Server restarted successfully.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            TrayLog.Write($"Server restart failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

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

    // Replaces the running server with one on this port for this scope. Called with the gate held.
    private async Task<bool> TryStartOnAsync(int port, ListenScope scope, CancellationToken cancellationToken)
    {
        SetPhase(ServerPhase.Starting);
        await StopProcessAsync().ConfigureAwait(false);
        _port = port;
        _scope = scope;
        try
        {
            StartProcess();
            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            SetPhase(ServerPhase.Running);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            TrayLog.Write($"The server did not start on port {port} ({scope.Describe()}): {ex.Message}");
            return false;
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
        var process = _process;
        if (process is null)
        {
            return;
        }
        _process = null;
        using (process)
        {
            await ServerProcessStop.StopAsync(process).ConfigureAwait(false);
        }
    }

    private void StartProcess()
    {
        var serverExe = FindServerExe();
        TrayLog.Write($"Starting bundled server host: {serverExe}");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverExe,
            Arguments = ServerListenArguments.For(_port, _scope),
            WorkingDirectory = Path.GetDirectoryName(serverExe),
            UseShellExecute = false,
            CreateNoWindow = true,
            // Kept in server-host.log (ServerOutputLog): a server that stops before its own log exists has nowhere else to say why.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ServerOutputLog.ServerEncoding(),
            StandardErrorEncoding = ServerOutputLog.ServerEncoding(),
        }) ?? throw new InvalidOperationException($"Failed to start {ServerExeName}");
        _output.Attach(process);

        Volatile.Write(ref _startedAtTicks, StartTimeUtc(process).Ticks);
        _process = process;
        TrayLog.Write($"Bundled server host pid={process.Id}");
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

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        try
        {
            await ServerHealth.WaitUntilReadyAsync(
                _http,
                new Uri($"http://127.0.0.1:{_port}/ready"),
                () => process is { HasExited: true } ? process.ExitCode : null,
                new ServerHealth.Timing(
                    _nothingAnswersTimeout,
                    ServerHealth.RetryDelay,
                    TimeProvider.System,
                    _startingTimeout,
                    () => IsSavingBeforeUpdate(process)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // A server that never became ready is not left running behind a tray that says it has stopped. Its process is kept, exited,
            // so the watchdog sees it and starts it again like any other that exited.
            TrayLog.Write($"{ex.Message} Stopping it.");
            if (process is not null)
            {
                TrayLog.Write($"Weir's server (pid {process.Id}) did not answer: {ServerProcessSnapshot.Describe(process)}. What it printed is in {ServerOutputLog.FileName}.");
            }

            if (process is { HasExited: false })
            {
                await ServerProcessStop.StopAsync(process, ServerStopTimeouts.Orphan).ConfigureAwait(false);
            }

            throw;
        }
    }

    // A server that is alive and keeps saying it is busy before it can answer (saving a copy of the data before an update) is
    // waited for for as long as its note stays fresh (StartupNotes.ProgressFreshFor); one that says nothing gets the timeouts above.
    private bool IsSavingBeforeUpdate(Process? process)
    {
        if (process is not { HasExited: false } || !StartupNotes.IsBusy(_runtimeHome, StartedAtUtc, DateTime.UtcNow))
        {
            _waitingForSave = false;
            return false;
        }
        if (!_waitingForSave)
        {
            _waitingForSave = true;
            TrayLog.Write("The server is saving a copy of Weir's data before updating; waiting for it to finish.");
        }
        return true;
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
