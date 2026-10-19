using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using Velopack;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

static class Program
{
    private const string MutexName = @"Local\WeirTrayHostSingleton";

    /// <summary>Where Weir keeps its data; the tray and the server both read it.</summary>
    internal const string RuntimeHomeVariable = "WEIR_HOME";

    /// <summary>System › About, where the web app checks for and installs updates.</summary>
    internal const string UpdateCheckPath = "/system?tab=about";

    /// <summary>
    /// Start without telling the person Weir is running. The starts nobody asked to see pass it: at sign-in, and after an
    /// update (#638). A start never opens the browser either way; only a click does.
    /// </summary>
    internal const string NoBrowserArgument = "--no-browser";

    /// <summary>
    /// Start with no UI at all, ever: no browser, no port dialog, no error message box, no matter what the desktop
    /// heuristic in <see cref="PortChoice.HasInteractiveDesktop"/> reports. This is what a program driving Weir
    /// unattended (an installer, a provisioning script) should pass, because a wrong "yes, there's a desktop"
    /// answer from that heuristic must never turn into a dialog nobody can see or answer (#779). A silent start
    /// that cannot get a usable port still exits with a non-zero code instead of asking.
    /// </summary>
    internal const string SilentArgument = "--silent";

    [STAThread]
    static int Main(string[] args)
    {
        // --configure-firewall, --remove-firewall and --allow-lan are Weir's own one-shot, headless commands, not
        // Velopack's install machinery — checked first, before RunInstallerHooks, so the elevated relaunch this
        // process itself starts for the first-run prompt (FirewallElevation, below) can never re-enter Velopack's
        // own first-run handling a second time. Whoever called them waits for this process to exit.
        if (FirewallCommand.Handles(args))
        {
            return FirewallCommand.Run(args, LogToConsoleAndFile);
        }

        RunInstallerHooks(args);

        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown");
            return 0;
        }

        // A start that follows Setup (RestartAfterSetup) is for a Weir that Setup stopped; when Setup has started one itself
        // there is nothing to do.
        if (RestartAfterSetup.ShouldStepAside(args, () => InstallProcesses.AnotherTrayRuns(InstallProcesses.Root(), TrayLog.Write, "Start after Setup")))
        {
            TrayLog.Write("Start after Setup: Weir is already running, so this start has nothing to do.");
            return 0;
        }

        using var mutex = new Mutex(false, MutexName, out bool createdNew);
        if (!createdNew)
        {
            HandOverToRunningTray(args, () => SecondLaunchSignal.Raise());
            return 0;
        }

        // Listening from here, not from when the icon exists: a second launch while this one asks for a port or installs
        // an update is told about, not dropped.
        using var secondLaunch = new SecondLaunchSignal();
        MarkRunning();
        LogUnhandledErrors();
        return RunTray(args, secondLaunch);
    }

    // Left behind only when something other than an orderly exit ends this tray (Setup stops it first, before any hook of
    // the new version runs), which is what lets the after-install hook know Weir was running (RestartAfterSetup).
    private static void MarkRunning()
    {
        var mark = TrayRunningMark.ForThisUser();
        mark.Set(TrayRun.ThisProcess());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => mark.Clear(Environment.ProcessId);
    }

    // Velopack runs Weir.exe with its own arguments to install, update and uninstall; these hooks run then and
    // exit, and a normal start falls through.
    private static void RunInstallerHooks(string[] args) => BuildInstallerHooks(args).Run();

    // Velopack installs a downloaded update by itself as the process starts, unless told not to. That would run
    // before UpdateOnStart, so the person's Notify-only choice, the stop of a server left running, and the
    // one-attempt-per-version record would all be skipped (#865). Turned off, UpdateOnStart is the only start-up
    // path that installs a waiting update.
    internal static VelopackApp BuildInstallerHooks(string[] args) =>
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback((v) =>
            {
                TrayLog.Write($"Velopack: after install v{v}");
                KillRunningProcesses($"Velopack after install v{v}");
                RestartAfterSetup.Schedule(
                    TrayRunningMark.ForThisUser(),
                    InstalledTray(),
                    run => run.IsRunning(),
                    ParentProcess.Id,
                    info => Process.Start(info)?.Dispose(),
                    TrayLog.Write);
            })
            .OnBeforeUninstallFastCallback((v) =>
            {
                TrayLog.Write($"Velopack: before uninstall v{v}");
                KillRunningProcesses($"Velopack before uninstall v{v}");
                TrayRunningMark.ForThisUser().Forget(InstalledTray());
                StartupRegistration.ForThisUser().Uninstall();
                FirewallInstallHooks.RemoveRuleIfElevated();
            })
            .OnBeforeUpdateFastCallback((v) =>
            {
                TrayLog.Write($"Velopack: before update to v{v}");
            })
            .OnAfterUpdateFastCallback((v) =>
            {
                TrayLog.Write($"Velopack: after update to v{v}");
                StartupRegistration.ForThisUser().RefreshIfEnabled();
            })
            // Unlike the FastCallback hooks above, OnFirstRun runs in-process as part of a normal app start and is
            // allowed to show UI (docs.velopack.io) — the one place Weir asks its one Windows admin (UAC) prompt
            // for LAN access, per the owner's decision, and whether to start with Windows. --silent Setup skips
            // Velopack's post-install app launch entirely (docs/release.md, #779), so this should never run during a
            // silent install either way; both questions check IsSilent and the interactive desktop themselves too,
            // rather than relying only on that.
            .OnFirstRun((v) =>
            {
                FirewallInstallHooks.AskOnFirstRun(args);
                StartWithWindowsPrompt.AskOnFirstRun(args);
            });

    // --configure-firewall, --remove-firewall and --allow-lan have no window to report to, so their outcome goes
    // to whatever console launched them (a script watching the exit code still wants a reason for it) as well as
    // tray-host.log, the same place every other startup decision is recorded.
    private static void LogToConsoleAndFile(string message)
    {
        TrayLog.Write(message);
        Console.Error.WriteLine(message);
    }

    // Weir is already running in this session: a second start opens nothing. The running tray says so from its own icon,
    // unless the start was silent, which stays silent.
    internal static void HandOverToRunningTray(string[] args, Action announceSecondLaunch)
    {
        TrayLog.Write("Tray host launch skipped: an existing Weir tray instance is already running.");
        if (PortChoice.SuppliedPort(args, Environment.GetEnvironmentVariable) is { } ignored)
        {
            TrayLog.Write($"Ignoring {ignored.Source} {ignored.Text}: Weir is already running. Use \"Change port\" from its tray menu, or quit it and start it again with the new port.");
        }
        if (!IsSilent(args))
        {
            announceSecondLaunch();
        }
    }

    private static int RunTray(string[] args, SecondLaunchSignal secondLaunch)
    {
        try
        {
            // Before anything else: whatever started Weir may have redirected our stdio through pipes
            // it is waiting to see closed. Weir runs until Quit, so holding those open would make that
            // wait never end (#779). See InheritedStdioHandles.
            InheritedStdioHandles.CloseInherited(TrayLog.Write);

            // Before any window, including the port dialog.
            ApplicationConfiguration.Initialize();

            var runtimeHome = RuntimeHome();
            if (!SecureRuntimeHome(args, runtimeHome))
            {
                return 1;
            }

            // Before the port is asked for and before any server runs: an update left waiting is installed while
            // nothing is running that the install could interrupt (#857).
            var updateService = new UpdateService(TrayLog.Write);
            UpdateSettings.SettleFirstRun(runtimeHome);
            var updateSettings = UpdateSettings.Load(runtimeHome);
            Func<RunningServer?> orphan = () => UpdateBackupReady.FindLiveServer(runtimeHome);
            var backup = new UpdateBackupHook(
                new UpdateBackupRequest(runtimeHome, TimeProvider.System, orphan).AskAsync,
                runtimeHome,
                () => orphan() is not null,
                reason => TrayLog.Write($"Weir did not update because it could not save a copy of its data: {reason}"));
            if (UpdateOnStart.TryApply(
                updateService, updateSettings.Mode, runtimeHome, StopOrphanedServers, backup, () => orphan() is not null))
            {
                return 0;
            }

            var resolution = ResolvePort(args, runtimeHome);
            if (resolution.Port is null && !resolution.Busy)
            {
                return 1;
            }
            var listenScope = LanAccessStartup.Resolve(runtimeHome, InstallProcesses.Root(), () => new ComFirewallPolicy(), TrayLog.Write);
            FirewallRuleWidening.AskInBackground(runtimeHome, listenScope, HasInteractiveDesktop(args, PortChoice.HasInteractiveDesktop));

            // A port.txt that is busy leaves the port unknown: the server host looks again with the same back-off as any start, and gives
            // up the same way (the balloon, the red icon, Restart Weir; or exit 1 with nobody at the desktop).
            using var app = new TrayApp(
                resolution.Port ?? 0,
                listenScope,
                new TrayStart(AnnouncesStart(args), HasInteractiveDesktop(args, PortChoice.HasInteractiveDesktop)),
                updateService,
                updateSettings,
                secondLaunch,
                resolution.Busy ? () => ResolvePort(args, runtimeHome) : null);
            return app.Run();
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
    /// A tray icon has no window to show an error in, so a failure in a menu handler or a background loop is
    /// written to tray-host.log and the tray keeps running, instead of WinForms' crash dialog or a silent exit.
    /// </summary>
    private static void LogUnhandledErrors()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => TrayLog.Write($"Unhandled error on the tray's UI thread:\n{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayLog.Write($"Unhandled error; the tray is stopping:\n{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TrayLog.Write($"Unobserved background error:\n{e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Creates the runtime home owner-only, or tightens it (RuntimeHomeSecurity). Returns false when Weir must not
    /// start because another account owns the folder; the person at the desktop, if any, is told why, unless
    /// <see cref="SilentArgument"/> rules out showing anything.
    /// </summary>
    private static bool SecureRuntimeHome(string[] args, string runtimeHome)
    {
        try
        {
            if (RuntimeHomeSecurity.Secure(runtimeHome))
            {
                TrayLog.Write($"Restricted {runtimeHome} to SYSTEM, Administrators and {Environment.UserName}.");
            }
            return true;
        }
        catch (RuntimeHomeOwnedByAnotherAccountException ex)
        {
            TrayLog.Write($"Not starting: {ex.Message}");
            if (HasInteractiveDesktop(args, PortChoice.HasInteractiveDesktop))
            {
                MessageBox.Show(ex.Message, "Weir", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    private static PortChoice.Resolution ResolvePort(string[] args, string runtimeHome)
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
        var interactive = HasInteractiveDesktop(args, PortChoice.HasInteractiveDesktop);

        // A server left behind by a tray that crashed still holds our port, and would make
        // the saved port look taken by "another program". This tray holds the session's
        // singleton mutex, so any WeirServer.exe from this install in this session is an orphan.
        StopOrphanedServers();

        var decision = PortChoice.Decide(
            supplied,
            saved,
            interactive,
            PortChoice.IsInUse,
            AskForPort);

        TrayLog.Write($"Port: {decision.Reason} (interactive desktop: {(interactive ? "yes" : "no")})");
        if (decision.Port is { } port && decision.Save)
        {
            PortChoice.Save(runtimeHome, port);
            TrayLog.Write($"Saved port {port} to {Path.Combine(runtimeHome, PortChoice.SavedPortFileName)}.");
        }
        return new PortChoice.Resolution(decision.Port, Busy: false);
    }

    // Weir is not running while this waits, so the icon is up, blinking, and a click on it brings the window forward.
    private static int? AskForPort(PortPrompt prompt)
    {
        using var waiting = new StartupIcon(
            size => LoadAppIcon(size),
            SystemInformation.SmallIconSize,
            () =>
            {
                if (Application.OpenForms.OfType<PortDialog>().FirstOrDefault() is { } open)
                {
                    ForegroundWindow.Bring(open);
                }
            });
        return PortDialog.Ask(prompt, PortChoice.IsInUse, LoadAppIcon());
    }

    private static void StopOrphanedServers() =>
        InstallProcesses.StopOwn(InstallProcesses.Root(), sameSessionOnly: true, TrayLog.Write, "Startup (orphaned server check)", ServerStopTimeouts.Orphan);

    /// <summary>The brand icon at its default size, for a window's title bar.</summary>
    internal static Icon LoadAppIcon() => LoadAppIcon(null);

    /// <summary>The brand icon's frame for <paramref name="size"/>, or the nearest one the icon file has.</summary>
    internal static Icon LoadAppIcon(Size? size)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("weir-tray-icon.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            return size is { } wanted ? new Icon(stream, wanted) : new Icon(stream);
        }

        var fileCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "weir-tray-icon.ico"),
            Path.Combine(AppContext.BaseDirectory, "assets", "weir-tray-icon.ico"),
        };
        foreach (var path in fileCandidates)
        {
            if (File.Exists(path))
            {
                return size is { } wanted ? new Icon(path, wanted) : new Icon(path);
            }
        }

        return FallbackIcon();
    }

    /// <summary>
    /// A copy of the system's application icon, for when the brand icon cannot be found. A copy, because whoever is given the icon
    /// disposes it, and the system's own instance is shared by every other caller.
    /// </summary>
    internal static Icon FallbackIcon() => (Icon)SystemIcons.Application.Clone();

    // Velopack's install and uninstall hooks: stop this install's own tray and server so their
    // files can be replaced or removed. Only this install's — see InstallProcesses.
    private static void KillRunningProcesses(string why) =>
        InstallProcesses.StopOwn(InstallProcesses.Root(), sameSessionOnly: false, TrayLog.Write, why, ServerStopTimeouts.Hook);

    // The Weir.exe these hooks run from: the install's own tray, which is what a mark must name to count for the install.
    private static string InstalledTray() => Environment.ProcessPath ?? Path.Combine(InstallProcesses.Root(), "Weir.exe");

    internal static string RuntimeHome()
    {
        var env = Environment.GetEnvironmentVariable(RuntimeHomeVariable)?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            return Path.GetFullPath(env);
        }
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(programData))
        {
            programData = @"C:\ProgramData";
        }
        return Path.Combine(programData, "Weir");
    }

    /// <summary>
    /// Whether a start with these arguments tells the person, with a balloon, that Weir is running once its server is
    /// ready. No start opens the browser: only a click does.
    /// </summary>
    internal static bool AnnouncesStart(IEnumerable<string> args) => !args.Contains(NoBrowserArgument) && !IsSilent(args);

    /// <summary>Whether <see cref="SilentArgument"/> was passed: no UI of any kind, ever, for this start.</summary>
    internal static bool IsSilent(IEnumerable<string> args) => args.Contains(SilentArgument);

    /// <summary>
    /// Whether there is a person to show UI to: <paramref name="desktopCheck"/>, unless <see cref="SilentArgument"/>
    /// overrides it. Silent always wins, because a start driven by another program must never gamble a hang on
    /// that heuristic being right. The check is passed in (as <see cref="PortChoice.Decide"/> does with its own
    /// side effects) so the override is testable without a real desktop.
    /// </summary>
    internal static bool HasInteractiveDesktop(IEnumerable<string> args, Func<bool> desktopCheck) => !IsSilent(args) && desktopCheck();

    /// <summary>
    /// Whether the first-run firewall prompt (<see cref="FirewallInstallHooks.AskOnFirstRun(string[])"/>) may show anything at all.
    /// Belt and suspenders alongside Setup's own <c>--silent</c> skipping the post-install app launch entirely
    /// (docs/release.md, #779): this never shows the prompt during a silent start or without an interactive
    /// desktop to show it on, whatever else changes about how this process was started.
    /// </summary>
    internal static bool ShouldPromptForFirewallAccess(IEnumerable<string> args, Func<bool> desktopCheck) =>
        HasInteractiveDesktop(args, desktopCheck);

    internal static bool OpenBrowser(
        int port,
        Action<ProcessStartInfo>? startProcess = null,
        string relativePath = "/")
    {
        if (!relativePath.StartsWith('/') || relativePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Browser path must be local to Weir.", nameof(relativePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = $"http://127.0.0.1:{port}{relativePath}",
            UseShellExecute = true,
        };

        try
        {
            (startProcess ?? (info => Process.Start(info)?.Dispose()))(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Opening a browser is a convenience action. Session 0, disconnected RDP
            // sessions, and hardened shell policies can reject shell execution; none of
            // those conditions should stop the tray watchdog or its server process.
            TrayLog.Write($"Could not open Weir in the browser: {ex.Message}");
            return false;
        }
    }
}
