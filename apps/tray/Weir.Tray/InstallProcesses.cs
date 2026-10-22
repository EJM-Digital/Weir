using System.ComponentModel;
using System.Diagnostics;

namespace Weir.Tray;

/// <summary>
/// Finding and stopping this install's own Weir processes — and only those.
///
/// A machine can run more than one Weir — a second install, a portable copy, a developer build —
/// so a process named <c>Weir</c> or <c>WeirServer</c> is stopped only when its executable is
/// inside this install's directory. If its path cannot be read (access denied, a different
/// user's process, already gone) it is left alone: not knowing is not permission.
/// </summary>
static class InstallProcesses
{
    private const string TrayName = "Weir";
    private const string ServerName = "WeirServer";

    internal static readonly string[] Names = [TrayName, ServerName];

    /// <summary>
    /// The directory this install runs from: the folder holding this Weir.exe. Velopack runs the
    /// app from <c>%LocalAppData%\Weir\current</c>, with the server under <c>current\server</c>,
    /// and runs its install and uninstall hooks from the same place.
    /// </summary>
    internal static string Root() => Normalize(AppContext.BaseDirectory);

    /// <summary>Full path, no trailing separator, for prefix comparison.</summary>
    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Whether <paramref name="executable"/> lives inside <paramref name="root"/>. A directory
    /// boundary is required, so <c>C:\Weir-old\Weir.exe</c> is not inside <c>C:\Weir</c>.
    /// </summary>
    internal static bool IsInside(string? executable, string root)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }
        string full;
        try
        {
            full = Path.GetFullPath(executable);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be normalized cannot be shown to be inside; StopOwn logs the process it leaves.
            return false;
        }
        var prefix = Normalize(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stop every running Weir or WeirServer whose executable is inside <paramref name="root"/>,
    /// except this process. <paramref name="sameSessionOnly"/> narrows it to this Windows session.
    /// Everything else is logged and left running. Returns the ids stopped.
    /// </summary>
    /// <summary>
    /// The servers go first, asked to stop through their stop event so they finish running jobs and close their
    /// database (#833, #868); the tray is stopped only after them, and without its process tree. Killing the tray
    /// with its tree first would end the server it started before it could be asked. A server that does not exit in
    /// time, or cannot be asked, is killed with its own tree as the fallback. The waits are bounded by
    /// <paramref name="timeouts"/>: a caller picks the budget that fits where it is called, because the install and
    /// uninstall hooks are ended by Velopack after 30 seconds and start-up has a person waiting.
    /// </summary>
    internal static List<int> StopOwn(string root, bool sameSessionOnly, Action<string> log, string why, ServerStopTimeouts timeouts)
    {
        var running = FindOwn(root, sameSessionOnly, log, why);
        try
        {
            var stopped = StopServers(running.Where(p => p.Name == ServerName).ToList(), timeouts, log, why);
            foreach (var tray in running.Where(p => p.Name == TrayName))
            {
                if (TryKill(tray, process => process.Kill(entireProcessTree: false), timeouts.AfterKill, log, why))
                {
                    stopped.Add(tray.Process.Id);
                }
            }
            return stopped;
        }
        finally
        {
            foreach (var own in running)
            {
                own.Process.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether a Weir tray other than this process is running from inside <paramref name="root"/>, in this Windows
    /// session, where it would hold the single-instance mutex.
    /// </summary>
    internal static bool AnotherTrayRuns(string root, Action<string> log, string why)
    {
        var running = FindOwn(root, sameSessionOnly: true, log, why);
        try
        {
            return running.Any(p => p.Name == TrayName);
        }
        finally
        {
            foreach (var own in running)
            {
                own.Process.Dispose();
            }
        }
    }

    // Every server is asked at once and the graceful wait is shared, so a second server does not add a second wait.
    private static List<int> StopServers(List<OwnProcess> servers, ServerStopTimeouts timeouts, Action<string> log, string why)
    {
        var stopped = new List<int>();
        var asked = servers.Where(server => ServerStopRequest.TrySend(server.Process.Id, out _)).ToList();
        var waiting = Stopwatch.StartNew();
        foreach (var server in asked)
        {
            if (ExitsWithin(server.Process, Remaining(timeouts.Graceful, waiting.Elapsed)))
            {
                stopped.Add(server.Process.Id);
                log($"{why}: stopped {server.Name} (pid {server.Process.Id}) at {server.Executable} cleanly.");
            }
        }

        foreach (var server in servers)
        {
            if (stopped.Contains(server.Process.Id))
            {
                continue;
            }

            if (TryKill(server, process => process.Kill(entireProcessTree: true), timeouts.AfterKill, log, why))
            {
                stopped.Add(server.Process.Id);
            }
        }
        return stopped;
    }

    /// <summary>What is left of a wait that has already run for <paramref name="spent"/>; none once it is used up.</summary>
    internal static TimeSpan Remaining(TimeSpan budget, TimeSpan spent) => budget > spent ? budget - spent : TimeSpan.Zero;

    internal static bool ExitsWithin(Process process, TimeSpan timeout)
    {
        try
        {
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryKill(OwnProcess own, Action<Process> kill, TimeSpan wait, Action<string> log, string why)
    {
        try
        {
            kill(own.Process);
            own.Process.WaitForExit(wait);
            log($"{why}: stopped {own.Name} (pid {own.Process.Id}) at {own.Executable}.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            log($"{why}: could not stop {own.Name} (pid {own.Process.Id}) at {own.Executable}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The processes of this install, not yet stopped. The caller disposes each one.</summary>
    internal static List<OwnProcess> FindOwn(string root, bool sameSessionOnly, Action<string> log, string why, Func<int>? currentSession = null)
    {
        var found = new List<OwnProcess>();
        var self = Environment.ProcessId;
        int? session = null;
        if (sameSessionOnly)
        {
            try
            {
                session = (currentSession ?? (() => Process.GetCurrentProcess().SessionId))();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
            {
                log($"{why}: stopped nothing: could not read this process's Windows session ({ex.Message}).");
                return found;
            }
        }

        foreach (var name in Names)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                if (IsOwn(proc, name, self, session, root, log, why, out var executable))
                {
                    found.Add(new OwnProcess(proc, name, executable));
                }
                else
                {
                    proc.Dispose();
                }
            }
        }
        return found;
    }

    internal static bool IsOwn(Process proc, string name, int self, int? session, string root, Action<string> log, string why, out string ownExecutable, Func<Process, string?>? executableOf = null)
    {
        ownExecutable = string.Empty;
        if (proc.Id == self)
        {
            return false;
        }

        string? executable;
        try
        {
            if (session is { } s && proc.SessionId != s)
            {
                return false;
            }

            executable = (executableOf ?? ExecutableOf)(proc);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            log($"{why}: left {name} (pid {proc.Id}) running: could not read where it runs from ({ex.Message}).");
            return false;
        }

        if (!IsInside(executable, root))
        {
            log($"{why}: left {name} (pid {proc.Id}) running: {executable ?? "unknown path"} is not part of this install ({root}).");
            return false;
        }
        ownExecutable = executable!;
        return true;
    }

    private static string? ExecutableOf(Process process) => PathOf(process.MainModule);

    /// <summary>Where a process runs from; a process that has no main module has no path.</summary>
    internal static string? PathOf(ProcessModule? module) => module?.FileName;

    internal sealed record OwnProcess(Process Process, string Name, string Executable);
}
