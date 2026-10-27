using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// The machine's TCP sockets at the moment a test ended in failure, for <c>sockets.txt</c> beside the rest of the evidence. A browser that
/// reports <c>net::ERR_NO_BUFFER_SPACE</c> (Windows' WSAENOBUFS) has run out of ephemeral ports or socket buffers, and the cause is whatever
/// holds them across the whole machine, not anything the failing test did: the suite itself keeps a few hundred sockets open at its peak. The
/// failed test is the only moment that can be looked at, so it records how many sockets each state has, which processes own them, and (on Windows)
/// whether the TCP/IP driver logged that it ran out of ephemeral ports (events 4231 and 4266) in the last hour.
/// </summary>
public static class SocketEvidence
{
    private const int TopProcesses = 15;

    public static string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"taken {DateTime.Now:HH:mm:ss.fff}");
        text.AppendLine();
        text.AppendLine("TCP connections by state:");
        foreach (var state in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().GroupBy(connection => connection.State).OrderByDescending(group => group.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {state.Key,-12} {state.Count()}");
        }

        if (!OperatingSystem.IsWindows())
        {
            return text.ToString();
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"TCP sockets by owning process and state (the {TopProcesses} that hold most):");
        text.AppendLine(ByOwner(Run("netstat", "-ano -p tcp")));
        text.AppendLine("Ephemeral-port events the TCP/IP driver logged in the last hour (none listed means none):");
        text.AppendLine(Run(
            "wevtutil",
            "qe System \"/q:*[System[Provider[@Name='Tcpip'] and (EventID=4231 or EventID=4266) and TimeCreated[timediff(@SystemTime) <= 3600000]]]\" /c:10 /rd:true /f:text"));
        return text.ToString();
    }

    /// <summary>The rows of <c>netstat -ano</c> counted per owning process and state, largest owners first.</summary>
    internal static string ByOwner(string netstat)
    {
        var rows = new List<(string State, int Pid)>();
        foreach (var line in netstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length == 5 && columns[0] == "TCP" && int.TryParse(columns[4], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                rows.Add((columns[3], pid));
            }
        }

        var owners = rows.GroupBy(row => row.Pid).OrderByDescending(group => group.Count()).Take(TopProcesses);
        var text = new StringBuilder();
        foreach (var owner in owners)
        {
            var states = string.Join(", ", owner.GroupBy(row => row.State).OrderByDescending(group => group.Count()).Select(group => $"{group.Key} {group.Count()}"));
            text.AppendLine(CultureInfo.InvariantCulture, $"  {NameOf(owner.Key)} (pid {owner.Key}): {owner.Count()} ({states})");
        }

        return text.ToString();
    }

    private static string NameOf(int pid)
    {
        if (pid == 0)
        {
            return "no process (closing or waiting out its time)";
        }

        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return "a process that has ended";
        }
    }

    private static string Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception problem) when (problem is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"({program} could not be run: {problem.Message})";
        }
    }
}
