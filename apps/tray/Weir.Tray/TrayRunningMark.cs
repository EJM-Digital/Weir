using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Weir.Tray;

/// <summary>A tray process: which one, when it started, and the Weir.exe it runs from.</summary>
readonly record struct TrayRun(int ProcessId, long StartedAtTicks, string Executable)
{
    /// <summary>This process.</summary>
    internal static TrayRun ThisProcess() => ThisProcess(Environment.ProcessPath);

    /// <param name="processPath">Where this process runs from; nothing is recorded for a process Windows names no path for.</param>
    internal static TrayRun ThisProcess(string? processPath)
    {
        using var self = Process.GetCurrentProcess();
        return new TrayRun(self.Id, self.StartTime.ToUniversalTime().Ticks, processPath ?? string.Empty);
    }

    /// <summary>Whether this very process, not a later one that was given its id, is still running.</summary>
    internal bool IsRunning() => IsRunning(Process.GetProcessById);

    /// <param name="open">Opens the process with the given id; throws <see cref="ArgumentException"/> when there is none.</param>
    internal bool IsRunning(Func<int, Process> open)
    {
        try
        {
            using var process = open(ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == StartedAtTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Whether a tray was running when Setup stopped it. Setup stops the running Weir before the new version's after-install
/// hook runs, so by then there is no process left to ask. A tray sets the mark when it starts and clears it when it exits
/// in order (Quit, an update, the end of the Windows session). A mark that is still there was left by something that
/// ended the tray without its knowing: Setup, or a crash.
///
/// The mark names the Weir.exe it was set by, and only an install's own Weir.exe counts for that install: a tray run from
/// a portable copy, the package folder or a development build leaves a mark that never makes a Setup start anything.
///
/// The key is volatile, so a restart or a sign-out never leaves a mark for a Weir that is not running. It sits directly
/// under Software because Windows makes every key created above a volatile one volatile too, and a stable key can't
/// then be created beneath it.
/// </summary>
sealed class TrayRunningMark(string keyPath)
{
    private const string DefaultKeyPath = @"SOFTWARE\WeirTrayRunning";
    private const string ProcessIdValueName = "ProcessId";
    private const string StartedAtValueName = "StartedAt";
    private const string ExecutableValueName = "Executable";

    /// <summary>The current user's mark.</summary>
    internal static TrayRunningMark ForThisUser() => new(DefaultKeyPath);

    // The hooks call these inside a time budget. A registry error is logged, never thrown: a missing mark costs Weir a
    // restart after a silent install, and failing the install over it would cost far more.

    /// <summary>Records that <paramref name="run"/> is running.</summary>
    internal void Set(TrayRun run)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
            key.SetValue(ProcessIdValueName, run.ProcessId, RegistryValueKind.DWord);
            key.SetValue(StartedAtValueName, run.StartedAtTicks, RegistryValueKind.QWord);
            key.SetValue(ExecutableValueName, run.Executable, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            TrayLog.Write($"Could not record that Weir is running: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the mark when it is this tray's own. Another tray that has started since has set its own, and an exiting
    /// tray must not take that away.
    /// </summary>
    internal void Clear(int processId)
    {
        try
        {
            if (Recorded()?.ProcessId == processId)
            {
                Remove();
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            TrayLog.Write($"Could not clear the record that Weir is running: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the mark when it was set by <paramref name="executable"/>, whether or not that tray is running. An
    /// uninstall stops the tray itself, and an install after it is a new one that must not start anything.
    /// </summary>
    internal void Forget(string executable)
    {
        try
        {
            if (Recorded() is { } recorded && SamePath(recorded.Executable, executable))
            {
                Remove();
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            TrayLog.Write($"Could not clear the record that Weir is running: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the tray of the install whose Weir.exe is <paramref name="executable"/> was running and has been ended
    /// without an orderly exit. A mark set by any other Weir.exe, or by a tray that is still running, is not that, and is
    /// left alone. Reading it removes it, so one mark answers one question.
    /// </summary>
    internal bool TakeLeftBy(string executable, Func<TrayRun, bool> isRunning)
    {
        try
        {
            if (Recorded() is not { } recorded || !SamePath(recorded.Executable, executable) || isRunning(recorded))
            {
                return false;
            }
            Remove();
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            TrayLog.Write($"Could not read whether Weir was running: {ex.Message}");
            return false;
        }
    }

    private static bool SamePath(string recorded, string executable)
    {
        if (recorded.Length == 0 || executable.Length == 0)
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(recorded), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private TrayRun? Recorded()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue(ProcessIdValueName) is int processId
            && key.GetValue(StartedAtValueName) is long startedAt
            && key.GetValue(ExecutableValueName) is string executable)
        {
            return new TrayRun(processId, startedAt, executable);
        }
        return null;
    }

    private void Remove() => Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
}
