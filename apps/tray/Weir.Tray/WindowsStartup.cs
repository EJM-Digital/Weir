using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using System.Diagnostics.CodeAnalysis;
using Velopack;

namespace Weir.Tray;

/// <summary>
/// What starting the tray asks of Windows that a test cannot answer: each is one line that would change this PC, raise a window
/// nobody could close, or take over the process. <see cref="StartupEffects.Windows"/> hands them to <see cref="TrayStartup"/>, so
/// a test hands it recorders instead and every line around them is run. The Windows scenario suite starts the real tray and runs
/// these themselves.
/// </summary>
static class WindowsStartup
{
    /// <summary>Velopack's hooks read the process's own command line and, for an install, update or uninstall, end the process.</summary>
    [ExcludeFromCodeCoverage]
    internal static void RunInstallerHooks(string[] args) => Program.BuildInstallerHooks(args).Run();

    /// <summary>Hears the next launch of Weir on this PC; a test that listened here would hear a real Weir's launches.</summary>
    [ExcludeFromCodeCoverage]
    internal static SecondLaunchSignal ListenForSecondLaunch() => new();

    /// <summary>Tells a Weir already running on this PC that it was started again; a test that told it would show its balloon.</summary>
    [ExcludeFromCodeCoverage]
    internal static void RaiseSecondLaunch() => SecondLaunchSignal.Raise();

    /// <summary>Windows Forms refuses to be told how to handle errors once a window exists, and the handlers it takes last for the process.</summary>
    /// <summary>Windows Forms refuses to be set up once any window has been made in the process.</summary>
    [ExcludeFromCodeCoverage]
    internal static void InitializeApplication() => ApplicationConfiguration.Initialize();

    /// <summary>Velopack's update manager can only be made by a process Velopack's installer started.</summary>
    [ExcludeFromCodeCoverage]
    internal static IUpdateService CreateUpdateService(Action<string> log) =>
        new UpdateService(new UpdateManager(UpdateChannel.SourceForThisBuild(ProjectLocation.RepositoryUrl)), log, Environment.Exit);

    /// <summary>Stops every server of this install in this session, which includes the stand-in servers other tests have running.</summary>
    [ExcludeFromCodeCoverage]
    internal static void StopOrphanedServers() =>
        InstallProcesses.StopOwn(InstallProcesses.Root(), sameSessionOnly: true, TrayLog.Write, "Startup (orphaned server check)", ServerStopTimeouts.Orphan);

    /// <summary>The icon waits in the notification area while the port window is open, and the window does not close until a person closes it.</summary>
    [ExcludeFromCodeCoverage]
    internal static int? AskForPort(PortPrompt prompt) => StartupQuestions.AskForPort(prompt, PortDialog.Ask);

    /// <summary>A modal message box.</summary>
    [ExcludeFromCodeCoverage]
    internal static void ShowError(string message) => MessageBox.Show(message, "Weir", MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>Runs the tray's message loop until Quit.</summary>
    [ExcludeFromCodeCoverage]
    internal static int RunApp(TrayLaunch launch)
    {
        using var app = new TrayApp(
            launch.Port,
            launch.ListenScope,
            launch.Start,
            launch.Updates,
            launch.UpdateSettings,
            launch.SecondLaunch,
            launch.ResolvePort);
        return app.Run();
    }

    /// <summary>Asks, off the calling thread, to widen the firewall rule when the start calls for it; the question is the owner's to answer.</summary>
    internal static void WidenFirewallRule(string runtimeHome, ListenScope scope, bool interactive) =>
        _ = FirewallRuleWidening.AskInBackground(runtimeHome, scope, interactive);

    /// <summary>Whether Setup has started a tray of this install itself.</summary>
    internal static bool AnotherTrayRuns() => InstallProcesses.AnotherTrayRuns(InstallProcesses.Root(), TrayLog.Write, "Start after Setup");

    /// <summary>Runs <paramref name="action"/> when the process ends in order.</summary>
    internal static void AtProcessExit(Action action) => AppDomain.CurrentDomain.ProcessExit += (_, _) => action();
}

/// <summary>The question Weir asks before it runs when it cannot decide the port itself.</summary>
static class StartupQuestions
{
    /// <summary>
    /// Weir is not running while this waits, so the icon is up, blinking, and a click on it brings the window forward.
    /// </summary>
    /// <param name="prompt">What to ask.</param>
    /// <param name="ask">Shows the port window with the brand icon; the chosen port, or null when the person closed it.</param>
    internal static int? AskForPort(PortPrompt prompt, Func<PortPrompt, Icon?, int?> ask)
    {
        using var waiting = new StartupIcon(
            size => Program.LoadAppIcon(size),
            SystemInformation.SmallIconSize,
            BringOpenDialogForward);
        return ask(prompt, Program.LoadAppIcon());
    }

    /// <summary>A click on the waiting icon: brings the port window forward.</summary>
    internal static void BringOpenDialogForward() => BringForward(Application.OpenForms.OfType<PortDialog>(), ForegroundWindow.Bring);

    /// <summary>Brings the first of the open port windows to the front; nothing when none is open.</summary>
    internal static void BringForward(IEnumerable<PortDialog> open, Action<Form> bring)
    {
        if (open.FirstOrDefault() is { } dialog)
        {
            bring(dialog);
        }
    }
}
