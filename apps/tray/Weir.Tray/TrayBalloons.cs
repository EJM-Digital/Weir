using System.Globalization;

namespace Weir.Tray;

/// <summary>How long a tray balloon stays: information briefly, a problem the person must read for longer.</summary>
static class TrayBalloons
{
    private const int InformationMs = 10_000;
    private const int ProblemMs = 30_000;

    internal static int TimeoutMs(ToolTipIcon icon) => icon is ToolTipIcon.Warning or ToolTipIcon.Error ? ProblemMs : InformationMs;

    /// <summary>The server exited and the watchdog gave up bringing it back. A click restarts it.</summary>
    internal const string StoppedText = "Weir stopped and couldn't start again by itself. Click to restart it.";

    /// <summary>Weir could not start, or could not start again from Restart Weir. A click tries again.</summary>
    internal const string CouldNotStartText = "Weir couldn't start. Click to try again.";

    /// <summary>"1 try" or "5 tries".</summary>
    internal static string Tries(int attempts) => attempts == 1 ? "1 try" : $"{attempts} tries";

    /// <summary>The supervision tried several times to start Weir and stopped trying. A click tries again.</summary>
    internal static string GaveUpText(int attempts) =>
        $"Weir didn't start after {Tries(attempts)}, so it has stopped trying. The details are in the tray log. Click to try again.";

    /// <summary>Weir stopped and the supervision could not start it again in several tries. A click tries again.</summary>
    internal static string StoppedAfterTriesText(int attempts) =>
        $"Weir stopped and didn't start again after {Tries(attempts)}. The details are in the tray log. Click to try again.";

    /// <summary>The supervision tried several times and stopped, because another program holds the port. A click tries again.</summary>
    internal static string PortInUseText(int port, int attempts) =>
        string.Create(CultureInfo.InvariantCulture, $"Weir didn't start after {Tries(attempts)} because another program is using port {port}. Close that program, or use Change port, then click to try again.");

    /// <summary>Weir stopped and could not start again, because another program holds the port. A click tries again.</summary>
    internal static string StoppedPortInUseText(int port, int attempts) =>
        string.Create(CultureInfo.InvariantCulture, $"Weir stopped and didn't start again after {Tries(attempts)} because another program is using port {port}. Close that program, or use Change port, then click to try again.");

    /// <summary>The person closed the window that asks for a port without choosing one, so there was nothing to start on. A click asks again.</summary>
    internal const string NoPortChosenText = "Weir didn't start because you closed the port window without choosing a port. Click to try again.";

    /// <summary>Weir could not start and said why, in its own words. A click tries again.</summary>
    internal static string CouldNotStartBecause(StartupError why) => $"Weir couldn't start. {why.Detail} Click to try again.";

    /// <summary>The server exited and could not be brought back, and said why. A click restarts it.</summary>
    internal static string StoppedBecause(StartupError why) => $"Weir stopped. {why.Detail} Click to try again.";

    /// <summary>A second request to apply an update arrived while the first was under way.</summary>
    internal const string UpdateUnderWayText = "An update is already being installed.";

    /// <summary>The person clicked the icon or Open Weir while the server was starting.</summary>
    internal const string StillStartingText = "Weir is still starting. Try again in a moment.";

    /// <summary>The person clicked the icon or Open Weir while the server was finishing its jobs to stop.</summary>
    internal const string StoppingText = "Weir is stopping. It finishes the work it is doing first.";

    /// <summary>The person clicked the icon or Open Weir while the server was stopped.</summary>
    internal const string NotRunningText = "Weir isn't running. Click to restart it.";

    /// <summary>Weir has started, and the person started it. A click opens it.</summary>
    internal static string RunningText(int port) => $"Weir is running at {TrayState.AddressFor(port)}. Click to open it.";

    /// <summary>Weir was started again while it was running. A click opens it.</summary>
    internal static string AlreadyRunningText(int port) => $"Weir is already running at {TrayState.AddressFor(port)}. Click to open it.";

    /// <summary>Change port worked. A click opens Weir at the new address.</summary>
    internal static string PortMovedText(int port) => $"Weir is now at {TrayState.AddressFor(port)}. Click to open it.";
}
