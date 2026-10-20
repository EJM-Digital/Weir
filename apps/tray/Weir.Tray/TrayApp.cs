using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>
/// The tray as a process: the server host, the Windows shell and the controller that decides what the shell shows, put
/// together and run on the thread that owns the message loop. What the tray does is <see cref="TrayController"/>'s; how it
/// reaches Windows is <see cref="WindowsTrayShell"/>'s.
/// </summary>
sealed class TrayApp : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ServerHost _server;
    private readonly WindowsTrayShell _shell;
    private readonly TrayController _controller;

    public TrayApp(
        int port,
        ListenScope listenScope,
        TrayStart start,
        IUpdateService updateService,
        UpdateSettings updateSettings,
        SecondLaunchSignal secondLaunch,
        Func<PortChoice.Resolution>? resolvePort = null)
        : this(port, listenScope, start, updateService, updateSettings, secondLaunch, ShellEffects.Windows, AppContext.BaseDirectory, resolvePort)
    {
    }

    internal TrayApp(
        int port,
        ListenScope listenScope,
        TrayStart start,
        IUpdateService updateService,
        UpdateSettings updateSettings,
        SecondLaunchSignal secondLaunch,
        ShellEffects effects,
        string installRoot,
        Func<PortChoice.Resolution>? resolvePort = null)
    {
        var runtimeHome = Program.RuntimeHome();
        _server = new ServerHost(runtimeHome, installRoot, port, listenScope);
        _shell = new WindowsTrayShell(effects);
        _controller = new TrayController(
            runtimeHome,
            start,
            _server,
            _shell,
            new WindowsFirewallAccess(),
            StartupRegistration.ForThisUser(),
            updateService,
            updateSettings,
            secondLaunch,
            TimeProvider.System,
            _cts,
            resolvePort);

        TrayLog.Write($"Starting tray host. installRoot={installRoot} runtimeHome={runtimeHome}");
    }

    /// <summary>Starts the server and runs the tray until Quit or an update restart. Returns the process exit code.</summary>
    public int Run()
    {
        _shell.AttachToThisThread();
        _controller.Begin();
        TrayLog.Write("Starting tray icon event loop");
        _shell.Run();
        TrayLog.Write("Tray icon event loop ended.");
        return _controller.ExitCode;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _controller.Dispose();
        _server.Dispose();
        _shell.Dispose();
        _cts.Dispose();
    }
}
