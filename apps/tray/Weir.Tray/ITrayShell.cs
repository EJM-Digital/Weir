namespace Weir.Tray;

/// <summary>What the person does to the tray icon, handed to the shell when it opens.</summary>
/// <param name="MenuClicks">What each menu item does when clicked.</param>
/// <param name="MenuOpening">The menu is about to be shown.</param>
/// <param name="IconClicked">The icon was clicked with this button.</param>
/// <param name="IconDoubleClicked">The icon was double-clicked.</param>
/// <param name="BalloonClicked">The balloon on screen was clicked.</param>
/// <param name="BlinkTick">The blink timer ticked.</param>
sealed record TrayShellHandlers(
    Dictionary<TrayMenuItem, Action> MenuClicks,
    Action MenuOpening,
    Action<MouseButtons> IconClicked,
    Action IconDoubleClicked,
    Action BalloonClicked,
    Action BlinkTick);

/// <summary>
/// Everything the tray's controller does to Windows: the notification-area icon with its menu and balloons, the blink timer,
/// the clipboard, the browser, File Explorer, the questions it asks, and ending the message loop. The controller decides
/// what to show and when; the shell only shows it.
/// </summary>
interface ITrayShell
{
    /// <summary>Makes the icon, its menu and the blink timer, and shows the icon.</summary>
    void Open(TrayShellHandlers handlers);

    /// <summary>Runs <paramref name="action"/> on the UI thread, after whatever it is doing at the moment.</summary>
    void Post(Action action);

    void ShowIcon(TrayIconKey key);

    void ShowHoverText(string text);

    void ShowMenu(IReadOnlyList<TrayMenuEntry> entries);

    void SetBlinking(bool on);

    void ShowBalloon(string title, string text, ToolTipIcon icon, int timeoutMs);

    /// <summary>Takes the icon out of the notification area.</summary>
    void Hide();

    /// <exception cref="System.Runtime.InteropServices.ExternalException">The clipboard is held by another program.</exception>
    void CopyToClipboard(string text);

    /// <summary>Opens the browser at Weir on <paramref name="port"/>; false when it could not.</summary>
    bool OpenBrowser(int port, string relativePath);

    void OpenFolder(string folder, string what);

    void RevealFile(string file);

    /// <summary>Asks which port to move to; null when the person closed the window.</summary>
    int? AskPort(PortPrompt prompt);

    /// <summary>Asks "Start Weir when you sign in to Windows?".</summary>
    bool AskStartWithWindows();

    /// <summary>Ends the thread's message loop.</summary>
    void ExitThread();

    /// <summary>Ends the application's message loops.</summary>
    void Exit();
}
