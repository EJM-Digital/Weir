using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>A hung server was asked to stop and killed, and is still running.</summary>
sealed class ServerWillNotStopException(int processId, string reason) : InvalidOperationException(reason)
{
    internal int ProcessId { get; } = processId;

    internal string Reason { get; } = reason;
}

/// <summary>The supervision gave up: the start limit was reached, or the server said why it cannot start.</summary>
sealed class StartGaveUpException(string message, int attempts, string reason) : InvalidOperationException(message)
{
    /// <summary>How many attempts were made in this run.</summary>
    internal int Attempts { get; } = attempts;

    /// <summary>Why the last one failed.</summary>
    internal string Reason { get; } = reason;
}

/// <summary>
/// One supervisor for every start of the server (<see cref="StartPolicy"/>, <see cref="StartSupervision"/>, <see cref="StartWatch"/>):
/// the first start, a start after the server crashed and Restart Weir all go through <see cref="SuperviseAsync"/>. An attempt
/// fails when the server exits or is hung, a server being hung when it stops making progress for longer than its phase allows
/// (it is stopped politely and then killed, and only a process this host started is ever touched). A failed attempt is
/// retried after a capped, jittered wait until the start limit is reached, and every attempt is written to tray-host.log and
/// server-host.log. A start that only worked on a retry is a warning in weir.log too, through the next server (StartAttemptNote).
/// A change of port or of who may connect starts the server once, without retries, and goes back to what worked if it fails.
/// </summary>
sealed partial class ServerHost
{
    /// <summary>The environment variable that tells a server it is a retry: its attempt number.</summary>
    internal const string AttemptVariable = "WEIR_START_ATTEMPT";

    /// <summary>The environment variable that tells a server why the earlier attempts failed.</summary>
    internal const string FailuresVariable = "WEIR_START_FAILURES";

    /// <summary>Starts the server, trying again as the supervision says, and returns once it is ready.</summary>
    /// <exception cref="StartGaveUpException">The start limit was reached.</exception>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SuperviseAsync(failedBefore: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the server cleanly and starts it again on the same port, for the same devices, with the whole budget of attempts
    /// again: this is the way back once the supervision has given up. Returns whether the server is back and ready.
    /// </summary>
    internal async Task<bool> RestartNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TrayLog.Write($"Restart: restarting the server on port {_port} ({_scope.Describe()}).");
            SetPhase(ServerPhase.Starting);
            await StopProcessAsync().ConfigureAwait(false);
            _supervision.Reset();
            _gaveUpAttempts = 0;
            _noPortChosen = false;
            try
            {
                await SuperviseAsync(failedBefore: null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (StartGaveUpException)
            {
                return false;
            }
        }
        finally
        {
            _gate.Release();
            RestartWatchdogIfStopped();
        }
    }

    /// <summary>
    /// Watches the server and, when it exits on its own, starts it again through the same supervision as any start. When the
    /// supervision gives up it calls <paramref name="onGaveUp"/>.
    /// </summary>
    internal void Watch(Action onGaveUp, CancellationToken cancellationToken)
    {
        _onGaveUp = onGaveUp;
        _watchdogToken = cancellationToken;
        LaunchWatchdog();
    }

    /// <summary>Whether the watchdog is running: it ends when the server is stopped on purpose and when it gives up, and a restart or a move starts it again.</summary>
    internal bool IsWatching => _watchdog is { IsCompleted: false };

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
        using var timer = new PeriodicTimer(WatchInterval, _time);
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

            if (!await RestartAfterExitAsync(exited, cancellationToken).ConfigureAwait(false))
            {
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

    // The server ran and then exited. That is a failed run like any other: counted, waited for, tried again, or given up on.
    // Returns whether the server is back and ready.
    private async Task<bool> RestartAfterExitAsync(Process exited, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A port change may have replaced the server while the watchdog waited.
            if (!ReferenceEquals(_process, exited))
            {
                return true;
            }

            var reason = $"exited with code {exited.ExitCode} after running {Seconds(_time.GetElapsedTime(_startedAtStamp))}";
            ForgetFailedProcess(exited);
            SetPhase(ServerPhase.Starting);
            await SuperviseAsync(failedBefore: reason, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StartGaveUpException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Makes attempts until the server is ready or the supervision gives up. Called with the gate held. failedBefore is a failure
    // that has already happened (the server crashed), which is counted first.
    private async Task SuperviseAsync(string? failedBefore, CancellationToken cancellationToken)
    {
        var failure = failedBefore;
        while (true)
        {
            if (failure is not null)
            {
                // Read once: what is logged and what is done are decided from the same reading.
                // A start that has not found its port has started no server, so there is nothing a server could have said.
                var said = _port == 0 ? null : ServerSaid();
                var decision = _supervision.Failed(failure);
                LogFailure(decision, said);
                if (decision.GiveUp || said is not null)
                {
                    throw GaveUp(decision, said);
                }

                SetPhase(ServerPhase.Starting);
                await Task.Delay(decision.Delay, _time, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                failure = await AttemptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ServerWillNotStopException stuck)
            {
                throw GaveUpOnStuckServer(stuck);
            }

            if (failure is null)
            {
                OnReady();
                return;
            }
        }
    }

    // The supervision stops here: the tray shows Stopped, with the reason, and Restart Weir is the way back.
    private StartGaveUpException GaveUp(StartDecision decision, StartupError? said)
    {
        var attempts = decision.Attempt;
        _gaveUpAttempts = attempts;
        SetPhase(ServerPhase.Stopped);
        return new StartGaveUpException(
            said is null
                ? $"Weir did not start after {attempts} attempts; the last failed: {decision.Reason}"
                : $"Weir cannot start: {said.Detail}",
            attempts,
            decision.Reason);
    }

    // A server that was asked to stop and killed, and is still running, still holds the port and the database. Another attempt would only
    // find them taken, so the trying stops here and says why.
    private StartGaveUpException GaveUpOnStuckServer(ServerWillNotStopException stuck)
    {
        var decision = _supervision.Failed(stuck.Reason);
        Say($"Start attempt {decision.Attempt} failed: {stuck.Reason}. Not trying again: another server would find this one still holding the port and the database. End process {stuck.ProcessId} (Task Manager, or restart the PC), then use Restart Weir.");
        _gaveUpAttempts = decision.Attempt;
        SetPhase(ServerPhase.Stopped);
        return new StartGaveUpException($"Weir did not start after {decision.Attempt} attempts; the last failed: {stuck.Reason}", decision.Attempt, stuck.Reason);
    }

    private void OnReady()
    {
        var attempt = _supervision.Attempt;
        var earlier = _supervision.Failures;
        _supervision.Ready();
        _gaveUpAttempts = 0;
        _noPortChosen = false;
        SetPhase(ServerPhase.Running);
        if (earlier.Count > 0)
        {
            // The start worked, on a retry. It is never quiet: the log words say "failed", the next server's weir.log has the warning.
            Say(string.Create(CultureInfo.InvariantCulture, $"Weir started on attempt {attempt}, after {earlier.Count} failed {(earlier.Count == 1 ? "attempt" : "attempts")}: {string.Join("; ", earlier)}."));
        }
    }

    private void LogFailure(StartDecision decision, StartupError? serverSaid)
    {
        var failures = string.Create(CultureInfo.InvariantCulture, $"{decision.FailuresInWindow} of {_policy.StartLimitBurst} failures allowed within {_policy.StartLimitInterval.TotalMinutes:0.#} min");
        if (serverSaid is not null)
        {
            Say($"Start attempt {decision.Attempt} failed: {decision.Reason}. The server said why it cannot start, so it is not tried again: {serverSaid.Detail}");
        }
        else if (decision.GiveUp)
        {
            Say(_lastAttemptFoundPortInUse
                ? $"Start attempt {decision.Attempt} failed: {decision.Reason}. Giving up ({failures}); Weir is not running. Close the program that is using port {_port}, or use Change port, then use Restart Weir."
                : $"Start attempt {decision.Attempt} failed: {decision.Reason}. Giving up ({failures}); Weir is not running. Restart Weir tries again.");
        }
        else
        {
            Say($"Start attempt {decision.Attempt} failed: {decision.Reason}. Trying again in {Seconds(decision.Delay)} ({failures}).");
        }
    }

    // To both logs that hold the start: the tray's, and the server's output, which has the failed server's own words beside it.
    private void Say(string line)
    {
        TrayLog.Write(line);
        _output.Write(line);
    }

    // One attempt: starts the server and waits for it to be ready for as long as it keeps making progress. Null when it is ready,
    // else why it failed. A hung server is stopped before this returns, so no attempt is left running behind the next.
    private async Task<string?> AttemptAsync(CancellationToken cancellationToken)
    {
        _lastAttemptFoundPortInUse = false;
        if (_port == 0 && await FindPortAsync(cancellationToken).ConfigureAwait(false) is { } noPort)
        {
            return noPort;
        }

        Process process;
        try
        {
            process = StartProcess();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or FileNotFoundException)
        {
            return $"could not be started: {ex.Message}";
        }

        var watch = new StartWatch(_policy, _time, process.Id);
        var feed = _feed!;
        try
        {
            await ServerHealth.WaitUntilReadyAsync(
                _http,
                new Uri($"http://127.0.0.1:{_port}/ready"),
                () => process.HasExited ? process.ExitCode : null,
                watch,
                () => (feed.Latest, StartProgressFile.Read(_runtimeHome)),
                ServerHealth.RetryDelay,
                _time,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ServerExitedException ex)
        {
            ForgetFailedProcess(process);
            var got = watch.Last is { } last ? $" (last phase: {last.Words})" : " (before it reported anything)";
            _lastAttemptFoundPortInUse = ex.ExitCode == ServerExitCodes.PortInUse;
            var because = _lastAttemptFoundPortInUse ? $": {PortInUseWords(_port)}" : string.Empty;
            return $"exited with code {ex.ExitCode} after running {Seconds(_time.GetElapsedTime(_startedAtStamp))}{got}{because}";
        }
        catch (ServerStartHungException ex)
        {
            // What the process was doing is recorded before it is stopped; then it is asked to stop and, failing that, killed with
            // its whole tree. Only this host's own process is ever stopped here.
            Say($"Weir's server (pid {process.Id}) did not answer: {ServerProcessSnapshot.Describe(process)}. What it printed is in {ServerOutputLog.FileName}.");
            var outcome = await StopHung(process).ConfigureAwait(false);
            var where = watch.Last is null ? "before it reported anything" : $"in phase '{ex.Verdict.Where}'";
            var hung = $"hung {where} for {Seconds(ex.Verdict.SilentFor)}";
            if (!IsGone(process, outcome))
            {
                throw new ServerWillNotStopException(process.Id, $"{hung}, and could not be stopped (process {process.Id} is still running)");
            }

            ForgetFailedProcess(process);
            return hung;
        }
    }

    // The port is not known yet (the saved one could not be read when the tray began). Null when it is known now; else why it is not. A
    // saved port that is busy is a failed attempt like any other, so it waits as long as any start does and gives up the same way. No
    // dialog and no save happen while it is busy. A person who closes the first-run dialog is not asked again.
    private async Task<string?> FindPortAsync(CancellationToken cancellationToken)
    {
        if (ResolvePort is not { } resolve)
        {
            return "no port is known";
        }

        var found = await resolve(cancellationToken).ConfigureAwait(false);
        if (found.Busy)
        {
            return $"{PortChoice.SavedPortFileName} could not be read (it stayed busy), so no port is known yet";
        }

        if (found.Port is not { } port)
        {
            var attempt = _supervision.Attempt;
            Say("No port was chosen, so Weir is not started. Restart Weir asks again.");
            _gaveUpAttempts = attempt;
            _noPortChosen = true;
            SetPhase(ServerPhase.Stopped);
            throw new StartGaveUpException("No port was chosen", attempt, "no port was chosen");
        }

        _port = port;
        WritePortFile();
        Say($"The saved port is readable again: port {port}.");
        return null;
    }

    // Stops a hung server: asked to stop, then killed with its whole tree. A test replaces it.
    internal Func<Process, Task<ServerStopOutcome>> StopHung { get; set; } =
        process => ServerProcessStop.StopAsync(process, ServerStopTimeouts.Orphan);

    // What the server said about why it cannot start, or null. A test replaces it.
    internal Func<StartupError?>? ServerSaidOverride { get; set; }

    private StartupError? ServerSaid() => ServerSaidOverride is { } over ? over() : StartupNotes.ReadError(_runtimeHome, StartedAtUtc);

    // Confirmed from the process itself, not from what the stop reported: a kill that did not take, or a stop that gave up waiting, leaves it running.
    // A process a quit has already stopped and disposed is gone.
    private static bool IsGone(Process process, ServerStopOutcome outcome)
    {
        if (outcome is ServerStopOutcome.AlreadyExited or ServerStopOutcome.StoppedCleanly)
        {
            return true;
        }

        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    // The attempt is over and the process with it. Whoever takes the process out of _process owns disposing it, so a quit that stops the
    // server at the same moment and the attempt never both do.
    private void ForgetFailedProcess(Process process)
    {
        if (TakeProcess(process))
        {
            process.Dispose();
        }
    }

    private static string PortInUseWords(int port) => string.Create(CultureInfo.InvariantCulture, $"port {port} is in use by another program");

    private static string Seconds(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.#} s");

    // Replaces the running server with one on this port for this scope, once: a change that does not work is undone by the caller,
    // not tried again. Called with the gate held.
    private async Task<bool> TryStartOnAsync(int port, ListenScope scope, CancellationToken cancellationToken)
    {
        SetPhase(ServerPhase.Starting);
        await StopProcessAsync().ConfigureAwait(false);
        _port = port;
        _scope = scope;

        // A change the person asked for is a start of its own: the earlier attempts and their failures are not this server's, and are not
        // passed to it as if they were.
        _supervision.Reset();
        _gaveUpAttempts = 0;
        string? failure;
        try
        {
            failure = await AttemptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServerWillNotStopException stuck)
        {
            Say($"The server did not start on port {port} ({scope.Describe()}): {stuck.Reason}. It is still running, so nothing is started beside it. End process {stuck.ProcessId}, then use Restart Weir.");
            SetPhase(ServerPhase.Stopped);
            return false;
        }

        if (failure is not null)
        {
            Say($"The server did not start on port {port} ({scope.Describe()}): {failure}");
            return false;
        }

        OnReady();
        return true;
    }

    private Process StartProcess()
    {
        var serverExe = FindServerExe();
        TrayLog.Write($"Starting bundled server host: {serverExe} (attempt {_supervision.Attempt})");

        // What the file holds is an earlier server's, whatever process id that server had.
        StartProgressFile.Clear(_runtimeHome);

        var start = new ProcessStartInfo
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
        };

        // A retry tells the server it is one and why, in this process's environment only; the server warns in weir.log.
        start.Environment[AttemptVariable] = null;
        start.Environment[FailuresVariable] = null;
        if (_supervision.Attempt > 1)
        {
            start.Environment[AttemptVariable] = _supervision.Attempt.ToString(CultureInfo.InvariantCulture);
            start.Environment[FailuresVariable] = string.Join("; ", _supervision.Failures);
        }

        // The server reports its progress on standard output as well as in the file; the tray owns that pipe.
        start.Environment[StartProgressFile.PipeVariable] = "1";

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {ServerExeName}");
        _feed = new StartProgressFeed(process.Id);
        _output.Attach(process, _feed);

        Volatile.Write(ref _startedAtTicks, StartTimeUtc(process).Ticks);
        _startedAtStamp = _time.GetTimestamp();
        lock (_processGate)
        {
            _process = process;
        }

        TrayLog.Write($"Bundled server host pid={process.Id}");
        return process;
    }
}
