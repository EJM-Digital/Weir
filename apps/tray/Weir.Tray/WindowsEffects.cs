using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Weir.Tray;

/// <summary>
/// The calls into Windows that a test cannot make: they start another program, change the person's clipboard or run the
/// message loop of the process. The shell takes them as <see cref="ShellEffects"/>, so a test hands it recorders instead and
/// everything around these one-line calls is run. The Windows scenario suite runs these themselves.
/// </summary>
static class WindowsEffects
{
    /// <summary>Starts a program, or opens a folder or an address in the program Windows keeps for it.</summary>
    [ExcludeFromCodeCoverage]
    internal static void StartProcess(ProcessStartInfo info) => Process.Start(info)?.Dispose();

    /// <exception cref="System.Runtime.InteropServices.ExternalException">Another program holds the clipboard.</exception>
    [ExcludeFromCodeCoverage]
    internal static void SetClipboardText(string text) => Clipboard.SetText(text);

    [ExcludeFromCodeCoverage]
    internal static void RunMessageLoop() => Application.Run();

    [ExcludeFromCodeCoverage]
    internal static void ExitThread() => Application.ExitThread();

    [ExcludeFromCodeCoverage]
    internal static void ExitApplication() => Application.Exit();
}

/// <summary>What <see cref="WindowsTrayShell"/> does through Windows itself; <see cref="Windows"/> is the real thing.</summary>
/// <param name="StartProcess">Starts a program, or opens a folder or an address.</param>
/// <param name="AskStartWithWindows">Asks "Start Weir when you sign in to Windows?" in a message box.</param>
/// <param name="AskPort">Shows the port window; the chosen port, or null when the person closed it. The icon is the window's own.</param>
/// <param name="SetClipboardText">Puts text on the clipboard.</param>
/// <param name="RunMessageLoop">Runs the message loop until the application exits.</param>
/// <param name="ExitThread">Ends the message loop of the thread.</param>
/// <param name="ExitApplication">Ends every message loop.</param>
sealed record ShellEffects(
    Action<ProcessStartInfo> StartProcess,
    Func<bool> AskStartWithWindows,
    Func<PortPrompt, Icon?, int?> AskPort,
    Action<string> SetClipboardText,
    Action RunMessageLoop,
    Action ExitThread,
    Action ExitApplication)
{
    internal static ShellEffects Windows { get; } = new(
        WindowsEffects.StartProcess,
        StartWithWindowsPrompt.AskInMessageBox,
        PortDialog.Ask,
        WindowsEffects.SetClipboardText,
        WindowsEffects.RunMessageLoop,
        WindowsEffects.ExitThread,
        WindowsEffects.ExitApplication);
}
