using System.Reflection;
using System.Security.AccessControl;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>
/// What starting the tray reaches outside itself: the one-shot firewall commands, Velopack's install hooks, the other launches of
/// Weir, the questions a person is asked and the tray itself. <see cref="Windows"/> is the real thing; a test hands recorders in
/// instead, so every decision between the first line of <c>Main</c> and the tray's message loop is run without a desktop.
/// </summary>
/// <param name="RunFirewallCommand">Runs <c>--configure-firewall</c>, <c>--remove-firewall</c> or <c>--allow-lan</c>; the arguments, where to report, the exit code.</param>
/// <param name="RunInstallerHooks">Velopack's install, update and uninstall hooks; a normal start falls through.</param>
/// <param name="AnotherTrayRuns">Whether another tray of this install is running.</param>
/// <param name="ListenForSecondLaunch">Starts hearing the launches of Weir that come after this one.</param>
/// <param name="RaiseSecondLaunch">Tells the running Weir that it was started again.</param>
/// <param name="OpenRunningMark">The record that Weir is running, which only a disorderly end leaves behind.</param>
/// <param name="AtProcessExit">Runs an action when this process ends in order.</param>
/// <param name="AttachErrorLog">Writes errors nobody handles to the tray's log.</param>
/// <param name="CloseInheritedStdio">Closes the stdio handles whatever started Weir lent it; where it reports.</param>
/// <param name="InitializeApplication">Sets up Windows Forms for the process.</param>
/// <param name="CreateUpdateService">Velopack's update service; where it reports.</param>
/// <param name="StopOrphanedServers">Stops a server left running by a tray that was killed.</param>
/// <param name="SecureRuntimeHome">Makes the data folder owner-only; whether it changed anything. Throws when another account owns it.</param>
/// <param name="HasInteractiveDesktop">Whether there is a person at a desktop to show anything to.</param>
/// <param name="IsInUse">Whether another program is using a port.</param>
/// <param name="AskForPort">Shows the port window and returns what the person chose, or null.</param>
/// <param name="OpenFirewall">Windows Firewall's rules.</param>
/// <param name="WidenFirewallRule">Asks, off this thread, to widen the firewall rule when the start calls for it.</param>
/// <param name="ShowError">Tells the person why Weir will not start.</param>
/// <param name="RunApp">Runs the tray until it ends and returns the exit code.</param>
sealed record StartupEffects(
    Func<string[], Action<string>, int> RunFirewallCommand,
    Action<string[]> RunInstallerHooks,
    Func<bool> AnotherTrayRuns,
    Func<SecondLaunchSignal> ListenForSecondLaunch,
    Action RaiseSecondLaunch,
    Func<TrayRunningMark> OpenRunningMark,
    Action<Action> AtProcessExit,
    Action AttachErrorLog,
    Action<Action<string>> CloseInheritedStdio,
    Action InitializeApplication,
    Func<Action<string>, IUpdateService> CreateUpdateService,
    Action StopOrphanedServers,
    Func<string, bool> SecureRuntimeHome,
    Func<bool> HasInteractiveDesktop,
    Func<int, bool> IsInUse,
    Func<PortPrompt, int?> AskForPort,
    Func<IFirewallPolicy> OpenFirewall,
    Action<string, ListenScope, bool> WidenFirewallRule,
    Action<string> ShowError,
    Func<TrayLaunch, int> RunApp)
{
    internal static StartupEffects Windows { get; } = new(
        FirewallCommand.Run,
        WindowsStartup.RunInstallerHooks,
        WindowsStartup.AnotherTrayRuns,
        WindowsStartup.ListenForSecondLaunch,
        WindowsStartup.RaiseSecondLaunch,
        TrayRunningMark.ForThisUser,
        WindowsStartup.AtProcessExit,
        UnhandledErrorLog.Attach,
        InheritedStdioHandles.CloseInherited,
        WindowsStartup.InitializeApplication,
        WindowsStartup.CreateUpdateService,
        WindowsStartup.StopOrphanedServers,
        RuntimeHomeSecurity.Secure,
        PortChoice.HasInteractiveDesktop,
        PortChoice.IsInUse,
        WindowsStartup.AskForPort,
        ComFirewallPolicy.Open,
        WindowsStartup.WidenFirewallRule,
        WindowsStartup.ShowError,
        WindowsStartup.RunApp);
}

/// <summary>Everything the tray runs with, as <see cref="TrayStartup"/> settled it.</summary>
/// <param name="Port">The port, or 0 when it is not known yet and <paramref name="ResolvePort"/> looks again.</param>
/// <param name="ListenScope">Who the server listens for.</param>
/// <param name="Start">How this start is told to the person.</param>
/// <param name="Updates">Velopack's update service.</param>
/// <param name="UpdateSettings">What the person chose about updates.</param>
/// <param name="SecondLaunch">Hears the launches that come after this one.</param>
/// <param name="ResolvePort">Looks for the port again; null when the port is known.</param>
sealed record TrayLaunch(
    int Port,
    ListenScope ListenScope,
    TrayStart Start,
    IUpdateService Updates,
    UpdateSettings UpdateSettings,
    SecondLaunchSignal SecondLaunch,
    Func<PortChoice.Resolution>? ResolvePort);

/// <summary>The order in which the tray starts, from its arguments to the tray itself.</summary>
sealed class TrayStartup(StartupEffects effects, string mutexName)
{
    /// <summary>Weir's single tray for this Windows session.</summary>
    internal const string SessionMutexName = @"Local\WeirTrayHostSingleton";

    private readonly StartupEffects _fx = effects;

    internal int Run(string[] args)
    {
        // --configure-firewall, --remove-firewall and --allow-lan are Weir's own one-shot, headless commands, not
        // Velopack's install machinery — checked first, before the installer hooks, so the elevated relaunch this
        // process itself starts for the first-run prompt (FirewallElevation) can never re-enter Velopack's
        // own first-run handling a second time. Whoever called them waits for this process to exit.
        if (FirewallCommand.Handles(args))
        {
            return _fx.RunFirewallCommand(args, LogToConsoleAndFile);
        }

        _fx.RunInstallerHooks(args);

        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            return 0;
        }

        // A start that follows Setup (RestartAfterSetup) is for a Weir that Setup stopped; when Setup has started one itself
        // there is nothing to do.
        if (RestartAfterSetup.ShouldStepAside(args, _fx.AnotherTrayRuns))
        {
            TrayLog.Write("Start after Setup: Weir is already running, so this start has nothing to do.");
            return 0;
        }

        using var mutex = new Mutex(false, mutexName, out bool createdNew);
        if (!createdNew)
        {
            Program.HandOverToRunningTray(args, _fx.RaiseSecondLaunch);
            return 0;
        }

        // Listening from here, not from when the icon exists: a second launch while this one asks for a port or installs
        // an update is told about, not dropped.
        using var secondLaunch = _fx.ListenForSecondLaunch();
        MarkRunning();
        _fx.AttachErrorLog();
        return RunTray(args, secondLaunch);
    }

    // --configure-firewall, --remove-firewall and --allow-lan have no window to report to, so their outcome goes
    // to whatever console launched them (a script watching the exit code still wants a reason for it) as well as
    // tray-host.log, the same place every other startup decision is recorded.
    internal static void LogToConsoleAndFile(string message)
    {
        TrayLog.Write(message);
        Console.Error.WriteLine(message);
    }

    // Left behind only when something other than an orderly exit ends this tray (Setup stops it first, before any hook of
    // the new version runs), which is what lets the after-install hook know Weir was running (RestartAfterSetup).
    private void MarkRunning()
    {
        var mark = _fx.OpenRunningMark();
        mark.Set(TrayRun.ThisProcess());
        _fx.AtProcessExit(() => mark.Clear(Environment.ProcessId));
    }

    private int RunTray(string[] args, SecondLaunchSignal secondLaunch)
    {
        try
        {
            // Before anything else: whatever started Weir may have redirected our stdio through pipes
            // it is waiting to see closed. Weir runs until Quit, so holding those open would make that
            // wait never end (#779). See InheritedStdioHandles.
            _fx.CloseInheritedStdio(TrayLog.Write);

            // Before any window, including the port dialog.
            _fx.InitializeApplication();

            var runtimeHome = Program.RuntimeHome();
            if (!SecureRuntimeHome(args, runtimeHome))
            {
                return 1;
            }

            // Before the port is asked for and before any server runs: an update left waiting is installed while
            // nothing is running that the install could interrupt (#857).
            var updateService = _fx.CreateUpdateService(TrayLog.Write);
            UpdateSettings.SettleFirstRun(runtimeHome);
            var updateSettings = UpdateSettings.Load(runtimeHome);
            Func<RunningServer?> orphan = () => UpdateBackupReady.FindLiveServer(runtimeHome);
            var backup = new UpdateBackupHook(
                new UpdateBackupRequest(runtimeHome, TimeProvider.System, orphan).AskAsync,
                runtimeHome,
                () => orphan() is not null,
                reason => TrayLog.Write($"Weir did not update because it could not save a copy of its data: {reason}"));
            if (UpdateOnStart.TryApply(
                updateService, updateSettings.Mode, runtimeHome, _fx.StopOrphanedServers, backup, () => orphan() is not null))
            {
                return 0;
            }

            var resolution = ResolvePort(args, runtimeHome);
            if (resolution.Port is null && !resolution.Busy)
            {
                return 1;
            }
            var listenScope = LanAccessStartup.Resolve(runtimeHome, InstallProcesses.Root(), _fx.OpenFirewall, TrayLog.Write);
            var interactive = Program.HasInteractiveDesktop(args, _fx.HasInteractiveDesktop);
            _fx.WidenFirewallRule(runtimeHome, listenScope, interactive);

            // A port.txt that is busy leaves the port unknown: the server host looks again with the same back-off as any start, and gives
            // up the same way (the balloon, the red icon, Restart Weir; or exit 1 with nobody at the desktop).
            return _fx.RunApp(new TrayLaunch(
                resolution.Port ?? 0,
                listenScope,
                new TrayStart(Program.AnnouncesStart(args), interactive),
                updateService,
                updateSettings,
                secondLaunch,
                resolution.Busy ? () => ResolvePort(args, runtimeHome) : null));
        }
        catch (Exception ex)
        {
            // The last boundary before the process ends. The tray has no console, so whatever start-up threw is
            // logged here or not at all.
            TrayLog.Write($"Fatal startup error:\n{ex}");
            return 1;
        }
    }

    /// <summary>
    /// Creates the runtime home owner-only, or tightens it (RuntimeHomeSecurity). Returns false when Weir must not
    /// start because another account owns the folder; the person at the desktop, if any, is told why, unless
    /// <see cref="Program.SilentArgument"/> rules out showing anything.
    /// </summary>
    internal bool SecureRuntimeHome(string[] args, string runtimeHome)
    {
        try
        {
            if (_fx.SecureRuntimeHome(runtimeHome))
            {
                TrayLog.Write($"Restricted {runtimeHome} to SYSTEM, Administrators and {Environment.UserName}.");
            }
            return true;
        }
        catch (RuntimeHomeOwnedByAnotherAccountException ex)
        {
            TrayLog.Write($"Not starting: {ex.Message}");
            if (Program.HasInteractiveDesktop(args, _fx.HasInteractiveDesktop))
            {
                _fx.ShowError(ex.Message);
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            // Weir still starts: its data works, it is only readable by more accounts than it should be.
            TrayLog.Write($"Could not restrict who can read {runtimeHome} ({ex.Message}); it keeps its current access list.");
            return true;
        }
    }

    /// <summary>
    /// The port to run on: supplied on the command line or in WEIR_PORT, else saved by an
    /// earlier run, else chosen now — by the person if there is one, by the default if not.
    /// Null means do not start; the reason is in tray-host.log.
    /// </summary>
    internal PortChoice.Resolution ResolvePort(string[] args, string runtimeHome)
    {
        var supplied = PortChoice.SuppliedPort(args, Environment.GetEnvironmentVariable);
        var savedPort = PortChoice.LoadSaved(runtimeHome);
        if (savedPort.Busy)
        {
            // Not knowing the saved port is not the same as there being none: going on would ask the first-run question again, or save
            // another port over the person's. Nothing is decided, asked or saved; the start looks again.
            TrayLog.Write($"Port: {PortChoice.SavedPortFileName} could not be read just now (it was being replaced or scanned), so no port is decided, asked for or saved.");
            return new PortChoice.Resolution(null, Busy: true);
        }

        var saved = savedPort.Port;
        var interactive = Program.HasInteractiveDesktop(args, _fx.HasInteractiveDesktop);

        // A server left behind by a tray that crashed still holds our port, and would make
        // the saved port look taken by "another program". This tray holds the session's
        // singleton mutex, so any WeirServer.exe from this install in this session is an orphan.
        _fx.StopOrphanedServers();

        var decision = PortChoice.Decide(
            supplied,
            saved,
            interactive,
            _fx.IsInUse,
            _fx.AskForPort);

        TrayLog.Write($"Port: {decision.Reason} (interactive desktop: {(interactive ? "yes" : "no")})");
        if (decision.Port is { } port && decision.Save)
        {
            PortChoice.Save(runtimeHome, port);
            TrayLog.Write($"Saved port {port} to {Path.Combine(runtimeHome, PortChoice.SavedPortFileName)}.");
        }
        return new PortChoice.Resolution(decision.Port, Busy: false);
    }
}
