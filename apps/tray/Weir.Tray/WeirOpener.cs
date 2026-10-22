namespace Weir.Tray;

/// <summary>
/// Opening Weir in the browser, which only ever follows a click: the icon, a menu item or a balloon. A click on the icon
/// or Open Weir opens it once if the server is running and otherwise says why it cannot; however a request arrives, two
/// within the debounce window open one window.
/// </summary>
/// <param name="phase">Where the server is now.</param>
/// <param name="port">The port the server is on now.</param>
/// <param name="debounce">Lets one request through per window.</param>
/// <param name="openBrowser">Opens the browser at a port and a path under Weir; false when it could not.</param>
/// <param name="balloon">Shows a balloon: title, text, icon and what a click on it does.</param>
/// <param name="restart">What a click on the balloon about a stopped server does.</param>
sealed class WeirOpener(
    Func<ServerPhase> phase,
    Func<int> port,
    Debounce debounce,
    Func<int, string, bool> openBrowser,
    Action<string, string, ToolTipIcon, Action?> balloon,
    Action restart)
{
    /// <summary>A click on the icon or on Open Weir: opens Weir, or says why it cannot yet.</summary>
    internal void OpenFromClick(string source)
    {
        switch (phase())
        {
            case ServerPhase.Running:
                Open(source);
                break;
            case ServerPhase.Starting:
                balloon("Weir", TrayBalloons.StillStartingText, ToolTipIcon.Info, null);
                break;
            case ServerPhase.Stopping:
                balloon("Weir", TrayBalloons.StoppingText, ToolTipIcon.Info, null);
                break;
            default:
                balloon("Weir", TrayBalloons.NotRunningText, ToolTipIcon.Warning, restart);
                break;
        }
    }

    /// <summary>Weir has started, and the person started it: says it is running, with the address. The start opens nothing; a click on the balloon opens Weir.</summary>
    internal void ShowRunning() =>
        balloon("Weir", TrayBalloons.RunningText(port()), ToolTipIcon.Info, () => Open("start-balloon"));

    /// <summary>Weir was started again while it was running: says so, with the address, and a click on the balloon opens Weir.</summary>
    internal void ShowAlreadyRunning() =>
        balloon("Weir", TrayBalloons.AlreadyRunningText(port()), ToolTipIcon.Info, () => Open("already-running-balloon"));

    /// <summary>Opens a page of Weir, unless a window was opened a moment ago.</summary>
    internal void Open(string source, string relativePath = "/")
    {
        if (!debounce.Allow())
        {
            TrayLog.Write($"Ignoring duplicate browser open request within debounce window (source={source}).");
            return;
        }
        TrayLog.Write($"Opening Weir in browser on port {port()} (source={source})");
        openBrowser(port(), relativePath);
    }
}
