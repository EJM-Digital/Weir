using System.Diagnostics;
using Weir.Host;

namespace Weir.Api.Tests;

/// <summary>
/// How a test ends a server it started: the way the product is stopped, then a wait for it to leave. Killing it is the last resort, taken
/// after <see cref="Ceiling"/> and only once <see cref="ChildDiagnostics"/> has kept what it was doing.
/// <para>
/// Under the coverage collector the server runs instrumented assemblies that write their hit counts when the process exits, behind a
/// named mutex shared by every <c>Weir.*</c> assembly on the machine. A process killed while it holds that mutex abandons it, and every
/// later server's exit fails on it (<see cref="CoverletExitMutexReproTests"/>). A server that is asked to stop finishes its flush and
/// leaves the mutex free.
/// </para>
/// </summary>
internal static class ChildServerStop
{
    /// <summary>
    /// How long a server is given to stop once asked. The host waits up to its shutdown timeout (30 seconds unless configured) for hosted
    /// services, and a server that is idle stops in well under a second; one that is still running after the host's own limit is stuck.
    /// </summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(35);

    /// <summary>Asks <paramref name="process"/> to stop and waits for it; returns where the diagnostics are when it had to be killed, otherwise an empty string.</summary>
    internal static string StopOrKill(Process process, string label)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited)
            {
                return string.Empty;
            }

            var waited = Stopwatch.StartNew();
            var asked = false;
            while (!process.WaitForExit(50))
            {
                // On Windows the stop event exists once the host has started, so the request is repeated until it is taken.
                asked = asked || Ask(process.Id);
                if (waited.Elapsed > Ceiling)
                {
                    break;
                }
            }

            if (process.HasExited)
            {
                NoteIfSlow(label, waited.Elapsed);
                return string.Empty;
            }

            var kept = ChildDiagnostics.Capture(process, label + " (stop)");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            return kept;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
            return string.Empty;
        }
    }

    // A server that takes long to stop is not a failure, but it is how a hang starts; the folder CI uploads keeps the count.
    private static void NoteIfSlow(string label, TimeSpan took)
    {
        if (took < TimeSpan.FromSeconds(5))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ChildDiagnostics.Folder);
            File.AppendAllText(
                Path.Join(ChildDiagnostics.Folder, "slow-stops.txt"),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O} {label} took {took.TotalSeconds:F1} s to stop{Environment.NewLine}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only a note.
        }
    }

    private static bool Ask(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var stop = EventWaitHandle.OpenExisting(StopRequest.EventName(processId));
                return stop.Set();
            }
            catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Elsewhere the server stops on SIGTERM, which .NET can send only through the platform's own tool.
        var start = new ProcessStartInfo("kill") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-TERM");
        start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var signal = Process.Start(start);
        signal?.WaitForExit();
        return true;
    }
}
