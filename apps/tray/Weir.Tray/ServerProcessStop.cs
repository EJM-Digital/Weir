using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Weir.Tray;

/// <summary>How a server process came to be stopped.</summary>
enum ServerStopOutcome
{
    AlreadyExited,

    /// <summary>It was asked to stop and shut down on its own.</summary>
    StoppedCleanly,

    /// <summary>It did not stop when asked, or could not be asked, and its process tree was killed.</summary>
    Killed,

    /// <summary>It did not stop when asked and could not be killed either.</summary>
    KillFailed,
}

/// <summary>How long a stop waits for the server to exit on its own, and then for a kill to take effect.</summary>
readonly record struct ServerStopTimeouts(TimeSpan Graceful, TimeSpan AfterKill)
{
    /// <summary>
    /// Quit and update: the graceful wait outlasts the 30 seconds the server's host allows its workers to finish when asked to
    /// stop, so a busy PC is never killed in the middle of a shutdown that is going to complete.
    /// </summary>
    internal static ServerStopTimeouts Default { get; } = new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5));

    /// <summary>
    /// Velopack's install and uninstall hooks, which it ends after 30 seconds: a server asked to stop, killed if it has not, and the
    /// tray killed after it, all inside 20.
    /// </summary>
    internal static ServerStopTimeouts Hook { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));

    /// <summary>A server left over from an earlier run, met at start-up, where the person is waiting for the tray to appear.</summary>
    internal static ServerStopTimeouts Orphan { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));

    /// <summary>The longest <see cref="InstallProcesses.StopOwn"/> can take with these: every server's graceful wait and kill, then the tray's kill.</summary>
    internal TimeSpan WorstCase => Graceful + AfterKill + AfterKill;
}

/// <summary>
/// Stopping the bundled server process: it is asked to stop (ServerStopRequest) so it shuts down through its host and
/// finishes running jobs and closes its database; only when it does not exit in time is its whole process tree killed.
/// </summary>
static class ServerProcessStop
{
    internal static Task<ServerStopOutcome> StopAsync(Process process) => StopAsync(process, ServerStopTimeouts.Default);

    /// <param name="process">The server process.</param>
    /// <param name="timeouts">How long to wait for it to exit on its own, and after a kill.</param>
    /// <param name="kill">Kills a process and its tree; the real kill when null.</param>
    internal static async Task<ServerStopOutcome> StopAsync(Process process, ServerStopTimeouts timeouts, Action<Process>? kill = null)
    {
        if (process.HasExited)
        {
            return ServerStopOutcome.AlreadyExited;
        }
        var started = Stopwatch.GetTimestamp();
        TrayLog.Write($"Stopping bundled server host pid={process.Id}");
        if (!ServerStopRequest.TrySend(process.Id, out var failure))
        {
            TrayLog.Write($"Bundled server host pid={process.Id} cannot be asked to stop ({failure}); killing it");
            return await KillAsync(process, timeouts.AfterKill, kill).ConfigureAwait(false);
        }
        if (await ExitsWithinAsync(process, timeouts.Graceful).ConfigureAwait(false))
        {
            TrayLog.Write($"Bundled server host pid={process.Id} stopped cleanly in {Seconds(Stopwatch.GetElapsedTime(started))}");
            return ServerStopOutcome.StoppedCleanly;
        }
        TrayLog.Write($"Bundled server host pid={process.Id} did not stop in {Seconds(timeouts.Graceful)}; killing it");
        return await KillAsync(process, timeouts.AfterKill, kill).ConfigureAwait(false);
    }

    private static async Task<ServerStopOutcome> KillAsync(Process process, TimeSpan wait, Action<Process>? kill)
    {
        try
        {
            (kill ?? (target => target.Kill(entireProcessTree: true)))(process);
            await ExitsWithinAsync(process, wait).ConfigureAwait(false);
            return ServerStopOutcome.Killed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            TrayLog.Write($"Could not kill the server process pid={process.Id}: {ex.Message}");
            return ServerStopOutcome.KillFailed;
        }
    }

    private static async Task<bool> ExitsWithinAsync(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal static string Seconds(TimeSpan duration) => $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
}
