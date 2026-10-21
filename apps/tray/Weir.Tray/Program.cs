using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Velopack;
using Weir.Tray.Firewall;

namespace Weir.Tray;

static class Program
{
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
    [ExcludeFromCodeCoverage]
    static int Main(string[] args) => new TrayStartup(StartupEffects.Windows, TrayStartup.SessionMutexName).Run(args);

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

    /// <summary>The brand icon at its default size, for a window's title bar.</summary>
    internal static Icon LoadAppIcon() => LoadAppIcon(null);

    /// <summary>The brand icon's frame for <paramref name="size"/>, or the nearest one the icon file has.</summary>
    internal static Icon LoadAppIcon(Size? size) => LoadAppIcon(size, Assembly.GetExecutingAssembly(), AppContext.BaseDirectory);

    /// <param name="size">The frame wanted, or null for the default.</param>
    /// <param name="assembly">Holds the icon as a resource.</param>
    /// <param name="baseDirectory">Where the icon file is looked for when the assembly does not hold it.</param>
    internal static Icon LoadAppIcon(Size? size, Assembly assembly, string baseDirectory)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("weir-tray-icon.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            return size is { } wanted ? new Icon(stream, wanted) : new Icon(stream);
        }

        var fileCandidates = new[]
        {
            Path.Combine(baseDirectory, "weir-tray-icon.ico"),
            Path.Combine(baseDirectory, "assets", "weir-tray-icon.ico"),
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
    [ExcludeFromCodeCoverage]
    private static void KillRunningProcesses(string why) =>
        InstallProcesses.StopOwn(InstallProcesses.Root(), sameSessionOnly: false, TrayLog.Write, why, ServerStopTimeouts.Hook);

    // The Weir.exe these hooks run from: the install's own tray, which is what a mark must name to count for the install.
    internal static string InstalledTray() => Environment.ProcessPath ?? Path.Combine(InstallProcesses.Root(), "Weir.exe");

    internal static string RuntimeHome() =>
        RuntimeHome(Environment.GetEnvironmentVariable, () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="commonApplicationData">Windows' folder for data shared by every account; empty when Windows names none.</param>
    internal static string RuntimeHome(Func<string, string?> environment, Func<string> commonApplicationData)
    {
        var env = environment(RuntimeHomeVariable)?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            return Path.GetFullPath(env);
        }
        var programData = commonApplicationData();
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
        Action<ProcessStartInfo> startProcess,
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
            startProcess(startInfo);
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
