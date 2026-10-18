using System.Diagnostics;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Asks a server to stop the way the platform's own tools do, so it shuts down in order instead of being killed. A coverage
/// collector writes what a process ran when the process ends on its own; a killed server leaves nothing behind.
/// </summary>
internal static class ServerShutdown
{
    /// <summary>Set to 1 when the run measures the server's coverage, so each server ends by being asked to stop.</summary>
    public const string CoverageVariable = "WEIR_CONTRACT_COVERAGE";

    public static bool CoverageIsMeasured => Environment.GetEnvironmentVariable(CoverageVariable) == "1";

    /// <summary>The server's own stop event on Windows, the termination signal elsewhere. False when the request could not be made.</summary>
    public static bool Request(Process server)
    {
        if (OperatingSystem.IsWindows())
        {
            return TrySetStopEvent(server.Id);
        }

        return SendTerminate(server.Id);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TrySetStopEvent(int processId)
    {
        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(StopEventName(processId));
            return stopEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    // The name the server listens on (Weir.Host StopRequest.EventName); this project references no server code.
    private static string StopEventName(int processId) => FormattableString.Invariant($"Local\\Weir-Stop-{processId}");

    private static bool SendTerminate(int processId)
    {
        var start = new ProcessStartInfo("kill") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-TERM");
        start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var kill = Process.Start(start);
        kill!.WaitForExit();
        return kill.ExitCode == 0;
    }
}
