using System.ComponentModel;
using System.Diagnostics;

namespace Weir.Tray.Firewall;

/// <summary>
/// Launches this same Weir.exe as <see cref="FirewallCommand.ConfigureArgument"/> or
/// <see cref="FirewallCommand.RemoveArgument"/>, elevated through Windows' own UAC consent prompt (the <c>runas</c>
/// verb). Used by both the first-run prompt and the tray's "Allow other devices..." menu item, so there is exactly
/// one place that knows how to ask for that one elevation.
/// </summary>
static class FirewallElevation
{
    /// <summary>The UAC consent dialog's own "No" (ERROR_CANCELLED).</summary>
    private const int CancelledByUser = 1223;

    internal enum Outcome
    {
        Configured,
        Declined,
        Failed,
    }

    internal static Outcome ConfigureElevated(Action<string> log) => RunElevated(FirewallCommand.ConfigureArgument, log);

    internal static Outcome RemoveElevated(Action<string> log) => RunElevated(FirewallCommand.RemoveArgument, log);

    private static Outcome RunElevated(string argument, Action<string> log) =>
        RunElevated(argument, log, Environment.ProcessPath, StartForReal);

    /// <summary>
    /// <paramref name="start"/> starts the process and returns what waits for it to end and gives its exit code, or null when no process
    /// started. It is a parameter so the reading of each way the step can end is tested without a Windows consent prompt.
    /// </summary>
    internal static Outcome RunElevated(string argument, Action<string> log, string? exe, Func<ProcessStartInfo, Func<int>?> start)
    {
        if (string.IsNullOrEmpty(exe))
        {
            log("Could not find Weir's own executable path to relaunch elevated for the firewall step.");
            return Outcome.Failed;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = argument,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            var waitForExit = start(startInfo);
            if (waitForExit is null)
            {
                log("The elevated firewall step did not start.");
                return Outcome.Failed;
            }
            var exitCode = waitForExit();
            if (exitCode == FirewallExitCode.Success)
            {
                return Outcome.Configured;
            }
            log($"The elevated firewall step exited with code {exitCode}.");
            return Outcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledByUser)
        {
            // The person clicked "No" on the UAC prompt: their choice, not a failure to alarm them with.
            log("The Windows admin prompt to allow Weir on the network was declined.");
            return Outcome.Declined;
        }
        catch (Win32Exception ex)
        {
            log($"Could not start the elevated firewall step: {ex.Message}");
            return Outcome.Failed;
        }
    }

    private static Func<int>? StartForReal(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }
        return () =>
        {
            using (process)
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        };
    }
}
