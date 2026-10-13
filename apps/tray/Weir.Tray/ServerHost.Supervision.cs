using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

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
            _process = null;
            exited.Dispose();
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
                var decision = _supervision.Failed(failure);
                LogFailure(decision, StartupNotes.ReadError(_runtimeHome, StartedAtUtc));
                if (decision.GiveUp || StartupNotes.ReadError(_runtimeHome, StartedAtUtc) is not null)
                {
                    throw GaveUp(decision);
                }

                SetPhase(ServerPhase.Starting);
                await Task.Delay(decision.Delay, _time, cancellationToken).ConfigureAwait(false);
            }

            failure = await AttemptAsync(cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                OnReady();
                return;
            }
        }
    }

    // The supervision stops here: the tray shows Stopped, with the reason, and Restart Weir is the way back.
    private StartGaveUpException GaveUp(StartDecision decision)
    {
        var attempts = decision.Attempt;
        var said = StartupNotes.ReadError(_runtimeHome, StartedAtUtc);
        _gaveUpAttempts = attempts;
        SetPhase(ServerPhase.Stopped);
        return new StartGaveUpException(
            said is null
                ? $"Weir did not start after {attempts} attempts; the last failed: {decision.Reason}"
                : $"Weir cannot start: {said.Detail}",
            attempts,
            decision.Reason);
    }

    private void OnReady()
    {
        var attempt = _supervision.Attempt;
        var earlier = _supervision.Failures;
        _supervision.Ready();
        _gaveUpAttempts = 0;
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
            Say($"Start attempt {decision.Attempt} failed: {decision.Reason}. Giving up ({failures}); Weir is not running. Restart Weir tries again.");
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
        Process process;
        try
        {
            process = StartProcess();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or FileNotFoundException)
        {
            return $"could not be started: {ex.Message}";
        }

        var watch = new StartWatch(_policy, _time, process.Id, new DateTimeOffset(StartTimeUtc(process), TimeSpan.Zero));
        var feed = _feed!;
        try
        {
            await ServerHealth.WaitUntilReadyAsync(
                _http,
                new Uri($"http://127.0.0.1:{_port}/ready"),
                () => process.HasExited ? process.ExitCode : null,
                watch,
                () => StartProgressFile.Newer(feed.Latest, StartProgressFile.Read(_runtimeHome)),
                ServerHealth.RetryDelay,
                _time,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ServerExitedException ex)
        {
            ForgetFailedProcess(process);
            var got = watch.Last is { } last ? $" (last phase: {last.Words})" : " (before it reported anything)";
            return $"exited with code {ex.ExitCode} after running {Seconds(_time.GetElapsedTime(_startedAtStamp))}{got}";
        }
        catch (ServerStartHungException ex)
        {
            // What the process was doing is recorded before it is stopped; then it is asked to stop and, failing that, killed with
            // its whole tree. Only this host's own process is ever stopped here.
            Say($"Weir's server (pid {process.Id}) did not answer: {ServerProcessSnapshot.Describe(process)}. What it printed is in {ServerOutputLog.FileName}.");
            await ServerProcessStop.StopAsync(process, ServerStopTimeouts.Orphan).ConfigureAwait(false);
            ForgetFailedProcess(process);
            var where = watch.Last is null ? "before it reported anything" : $"in phase '{ex.Verdict.Where}'";
            return $"hung {where} for {Seconds(ex.Verdict.SilentFor)}";
        }
    }

    private void ForgetFailedProcess(Process process)
    {
        if (ReferenceEquals(_process, process))
        {
            _process = null;
        }

        process.Dispose();
    }

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
        var failure = await AttemptAsync(cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            Say($"The server did not start on port {port} ({scope.Describe()}): {failure}");
            return false;
        }

        SetPhase(ServerPhase.Running);
        return true;
    }

    private Process StartProcess()
    {
        var serverExe = FindServerExe();
        TrayLog.Write($"Starting bundled server host: {serverExe} (attempt {_supervision.Attempt})");

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
        _process = process;
        TrayLog.Write($"Bundled server host pid={process.Id}");
        return process;
    }
}
