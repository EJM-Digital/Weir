using System.ComponentModel;
using System.Runtime.InteropServices;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>
/// What the tray decides: what its icon, hover text and menu say, what each click does, which balloons it shows and when,
/// and how the server is started, watched, restarted and stopped. Everything that touches Windows goes through
/// <see cref="ITrayShell"/>, and everything runs on the UI thread: work on other threads (the server watchdog, update
/// checks, a port change) hands its result back through <see cref="OnUi"/>. The icon, its hover text and the menu are all
/// drawn from one place, <see cref="Render"/>, from the state the controller keeps, so they cannot disagree.
/// </summary>
sealed class TrayController : IDisposable
{
    private static readonly TimeSpan BrowserDebounceWindow = TimeSpan.FromMilliseconds(1250);

    private readonly string _runtimeHome;
    private readonly TrayStart _start;
    private readonly ITrayServer _server;
    private readonly ITrayShell _shell;
    private readonly LanAccessSync _lanAccess;
    private readonly UpdateSettings _updateSettings;
    private readonly IUpdateService _updateService;
    private readonly TrayShutdown _shutdown;
    private readonly SecondLaunchSignal _secondLaunch;
    private readonly StartupRegistration _startup;
    private readonly TrayStatusGate _serverStatus;
    private readonly TrayBlink _blink = new();
    private readonly TrayStatusDeadline _statusDeadline;
    private readonly WeirOpener _opener;
    private readonly PauseControl _pause;
    private readonly PortChange _portChange;
    private readonly IFirewallAccess _firewall;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts;
    private readonly TrayUpdates _updates;
    private readonly LanAccessMenu _lanAccessMenu;

    private TrayStatusWatcher? _statusWatcher;
    private bool _opened;
    private bool _watching;
    private readonly BalloonPresenter _balloons;
    private readonly TrayIconClicks _iconClicks;

    /// <param name="runtimeHome">Where Weir keeps its data, and the tray its log and the files it shares with the server.</param>
    /// <param name="start">How this run was started.</param>
    /// <param name="server">The bundled server.</param>
    /// <param name="shell">Everything the controller does to Windows.</param>
    /// <param name="firewall">Windows Firewall, for the LAN access choice.</param>
    /// <param name="startup">The "Start with Windows" entry.</param>
    /// <param name="updateService">Velopack.</param>
    /// <param name="updateSettings">The operator's update choices.</param>
    /// <param name="secondLaunch">Tells the tray another copy was started.</param>
    /// <param name="time">The clock every wait here is measured by.</param>
    /// <param name="shutdown">Cancelled when the tray is ending, which ends every loop here.</param>
    /// <param name="resolvePort">
    /// Finds the port when it was not known at the start (port.txt was busy): each start attempt asks again, on the UI thread,
    /// where a dialog can be shown. Null when the port is known.
    /// </param>
    internal TrayController(
        string runtimeHome,
        TrayStart start,
        ITrayServer server,
        ITrayShell shell,
        IFirewallAccess firewall,
        StartupRegistration startup,
        IUpdateService updateService,
        UpdateSettings updateSettings,
        SecondLaunchSignal secondLaunch,
        TimeProvider time,
        CancellationTokenSource shutdown,
        Func<PortChoice.Resolution>? resolvePort = null)
    {
        _runtimeHome = runtimeHome;
        _start = start;
        _server = server;
        _shell = shell;
        _balloons = new BalloonPresenter(new ShellBalloons(shell), TrayLog.Write);
        _firewall = firewall;
        _startup = startup;
        _secondLaunch = secondLaunch;
        _time = time;
        _cts = shutdown;
        if (resolvePort is not null)
        {
            _server.ResolvePort = _ => ResolveOnUiAsync(resolvePort);
        }
        _lanAccess = new LanAccessSync(_runtimeHome, _server, _firewall, time);
        _updateSettings = updateSettings;
        _updateService = updateService;
        _statusDeadline = new TrayStatusDeadline(time.GetUtcNow);
        _shutdown = new TrayShutdown(
            StopServerAsync,
            updateService,
            new UpdateBackupHook(
                new UpdateBackupRequest(
                    _runtimeHome,
                    time,
                    () => _server.Running,
                    message => OnUi(() => ShowBalloon(UpdateNotices.AvailableTitle, message, ToolTipIcon.Info))).AskAsync,
                _runtimeHome,
                () => _server.ServerIsRunning,
                reason => OnUi(() => ShowNotUpdated(reason))));
        _serverStatus = new TrayStatusGate(() => _server.StartedAtUtc);
        _opener = new WeirOpener(
            () => _server.Phase,
            () => _server.Port,
            new Debounce(BrowserDebounceWindow, time),
            _shell.OpenBrowser,
            ShowBalloon,
            RestartObserved);
        _iconClicks = new TrayIconClicks(_opener);
        _pause = new PauseControl(_runtimeHome, time, () => State().IsPaused, ShowBalloon, ShowTrayLog);
        _portChange = new PortChange(
            () => _server.Port,
            _shell.AskPort,
            _server.MoveToPortAsync,
            ShowBalloon,
            () => _opener.Open("port-change-balloon"),
            ShowTrayLog,
            Render,
            _cts.Token);
        _lanAccessMenu = new LanAccessMenu(_lanAccess, _server, _firewall, ShowLanAccessNotice, Render, _cts.Token);
        _updates = new TrayUpdates(
            _updateService,
            _runtimeHome,
            _updateSettings,
            new UpdateCallbacks(
                OnUi,
                Render,
                ShowUpdateNotice,
                () => BackgroundWork.Observe("Apply update", ApplyUpdateAndRestartAsync())),
            _time,
            _cts.Token,
            () => UpdateSettings.Load(_runtimeHome));
    }

    /// <summary>The code the process ends with: 1 when a start with nobody at the desktop could not get the server up.</summary>
    internal int ExitCode { get; private set; }

    /// <summary>
    /// Prepares the server's environment, shows the icon, and queues the server's start and the start-with-Windows question on
    /// the UI thread, which runs them once its message loop does.
    /// </summary>
    internal void Begin()
    {
        _server.PrepareEnvironment();
        if (_server.Port > 0)
        {
            _server.WritePortFile();
        }
        TrayLog.Write(_server.Port > 0 ? $"Prepared runtime environment on port {_server.Port}" : "Prepared runtime environment; the port is not known yet");

        // The icon is there from the first moment, marked Starting, not only once the server answers.
        CreateTrayIcon();
        _server.PhaseChanged += () =>
        {
            var phase = _server.Phase;
            OnUi(() => ShowPhase(phase));
        };
        _statusWatcher = new TrayStatusWatcher(_runtimeHome, reading => OnUi(() => ShowServerStatus(reading)));
        _statusWatcher.Start();
        _secondLaunch.Subscribe(() => OnUi(ShowAlreadyRunning));

        // Start-up runs inside the message loop, so waiting for the server never blocks the UI thread.
        OnUi(() => BackgroundWork.Observe("Start-up", StartAsync()));

        // The icon is up and the server is starting before anything is asked: an entry an older version set at install, with
        // nobody asked, is asked about once, on a start the person made.
        OnUi(AskAboutStartWithWindows);
    }

    internal async Task StartAsync()
    {
        TrayLog.Write("Waiting for local health endpoint");
        try
        {
            await _server.StartAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or FileNotFoundException or Win32Exception)
        {
            TrayLog.Write($"Fatal startup error:\n{ex}");
            if (!_start.Interactive)
            {
                TrayLog.Write($"Nobody is at the desktop to tell, so Weir exits with code 1: {ex.Message}");
                ExitCode = 1;
                _shell.ExitThread();
                return;
            }

            // The person is there: say so, and keep the icon, whose Restart Weir is the way back.
            ShowBalloon("Weir", CouldNotStartText(), ToolTipIcon.Error, RestartObserved);
            return;
        }
        TrayLog.Write($"Weir is healthy on http://127.0.0.1:{_server.Port}/");

        // A start never opens the browser; the balloon offers it, and a click opens it.
        if (_start.AnnounceOnReady)
        {
            _opener.ShowRunning();
        }
        else
        {
            TrayLog.Write("Skipping the start balloon (no-browser mode).");
        }

        // An update an earlier run could not apply, for want of a copy of the data, is still waiting: say so now the icon is up.
        if (UpdateBackupHook.PendingReason(_runtimeHome, AppVersion.Current) is { } notUpdated)
        {
            ShowNotUpdated(notUpdated);
        }

        BeginWatching();
    }

    // Once the server has been up: watch it, follow the LAN access choice and look for updates. Run once, whichever
    // start, first or Restart Weir after a failed one, gets the server up.
    private void BeginWatching()
    {
        if (_watching)
        {
            return;
        }
        _watching = true;

        _server.Watch(
            () => OnUi(() => ShowBalloon(
                "Weir",
                StoppedText(),
                ToolTipIcon.Error,
                RestartObserved)),
            _cts.Token);

        _ = BackgroundWork.RunLoop(
            "LAN access watcher",
            ct => _lanAccess.WatchAsync(
                new LanAccessWatch(
                    () => OnUi(() => _lanAccessMenu.OnWaitingForWindows()),
                    applied => OnUi(() => _lanAccessMenu.OnChangedElsewhere(applied))),
                ct),
            _cts.Token);

        _updates.Start();
    }

    private Task<PortChoice.Resolution> ResolveOnUiAsync(Func<PortChoice.Resolution> resolve)
    {
        var done = new TaskCompletionSource<PortChoice.Resolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            try
            {
                done.SetResult(resolve());
            }
#pragma warning disable CA1031 // Whatever it was, the waiting start is told.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, after whatever it is doing at the moment.</summary>
    private void OnUi(Action action) => _shell.Post(action);

    // -- What the tray shows ------------------------------------------------

    internal TrayState State()
    {
        var phase = _server.Phase;
        var status = _serverStatus.Current;
        _statusDeadline.Follow(waiting: phase == ServerPhase.Running && status is null);
        return new TrayState(
            phase,
            status,
            _updates.IsDownloaded ? _updates.PendingVersion : null,
            _server.Port,
            _statusDeadline.Overdue,
            _server.StartupError ?? _server.GaveUpError);
    }

    // The server's own reason when it said one (a copy of the data it could not save before updating), else the general text.
    internal string CouldNotStartText() =>
        _server.StartupError is { } why ? TrayBalloons.CouldNotStartBecause(why)
        : _server.NoPortChosen ? TrayBalloons.NoPortChosenText
        : _server.GaveUpPortInUse is > 0 and var held ? TrayBalloons.PortInUseText(held, _server.GaveUpAfterAttempts)
        : _server.GaveUpAfterAttempts is > 0 and var tries ? TrayBalloons.GaveUpText(tries)
        : TrayBalloons.CouldNotStartText;

    // The same, for a server that ran and then could not be brought back.
    internal string StoppedText() =>
        _server.StartupError is { } why ? TrayBalloons.StoppedBecause(why)
        : _server.GaveUpPortInUse is > 0 and var held ? TrayBalloons.StoppedPortInUseText(held, _server.GaveUpAfterAttempts)
        : _server.GaveUpAfterAttempts is > 0 and var tries ? TrayBalloons.StoppedAfterTriesText(tries)
        : TrayBalloons.StoppedText;

    internal void AskAboutStartWithWindows()
    {
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(_start.StartedByPerson, _shell.AskStartWithWindows, _startup);
        Render();
    }

    // The icon's dot and mark, the hover text and the menu, from the state as it is now.
    internal void Render()
    {
        if (!_opened)
        {
            return;
        }
        var state = State();
        _blink.Follow(state.Dot);
        _shell.SetBlinking(_blink.Active);
        ShowIcon(state);
        _shell.ShowHoverText(state.HoverText);
        _shell.ShowMenu(TrayMenu.Describe(new TrayMenuInputs(
            state,
            _updates.MenuState(),
            _lanAccessMenu.State,
            _portChange.MovingTo,
            _startup.IsEnabled,
            AppVersion.Current)));
    }

    // Only the icon: the blink swaps between icons already made, and leaves the hover text and the menu alone.
    private void ShowIcon(TrayState state) => _shell.ShowIcon(state.IconKey(_blink.Lit));

    // A server that is starting has said nothing yet: what the one before it left in tray-status.json is not shown.
    internal void ShowPhase(ServerPhase phase)
    {
        if (phase == ServerPhase.Starting)
        {
            _serverStatus.ServerStarting();
        }
        Render();
    }

    internal void ShowServerStatus(TrayStatusReading reading)
    {
        _serverStatus.Offer(reading);
        Render();
    }

    private void OnBlinkTick()
    {
        // A status that has not come in time ends the blink, and that changes more than the icon. A dot that has turned to starting
        // since the last drawing begins to blink with that drawing.
        var state = State();
        if (state.Dot != TrayDot.Starting || !_blink.Active)
        {
            Render();
            return;
        }

        _blink.Tick();
        ShowIcon(state);
    }

    // -- Updates ------------------------------------------------------------

    internal void ShowUpdateNotice(UpdateMode mode)
    {
        var notice = UpdateNotices.For(
            mode,
            _updates.PendingVersion ?? "new version",
            restartToUpdate: () =>
            {
                TrayLog.Write("Restart to update chosen from the update notice.");
                BackgroundWork.Observe("Apply update", ApplyUpdateAndRestartAsync());
            },
            openAbout: () => _opener.Open("tray-update-balloon", Program.UpdateCheckPath));
        TrayLog.Write(notice.Log);
        ShowBalloon(notice.Title, notice.Text, ToolTipIcon.Info, notice.OnClick);
        Render();
    }

    private void OnUpdateMenuClick()
    {
        switch (_updates.MenuState().Action)
        {
            case UpdateMenuAction.OpenUpdatePage:
                _opener.Open("tray-update-settings", Program.UpdateCheckPath);
                break;
            case UpdateMenuAction.Check:
                _ = _updates.CheckInBackground(asked: true);
                break;
            case UpdateMenuAction.Download:
                _ = _updates.DownloadInBackground(asked: true);
                break;
            case UpdateMenuAction.Restart:
                TrayLog.Write("Restart to update chosen from the tray menu.");
                BackgroundWork.Observe("Apply update", ApplyUpdateAndRestartAsync());
                break;
            default:
                break;
        }
    }

    internal Task ApplyUpdateAndRestartAsync() =>
        new UpdateRestart(() => _updates.IsDownloaded, _shutdown.RestartToUpdateAsync, ShowBalloon, TrayLog.Write).RunAsync();

    // The update stays downloaded and the old version keeps running; the next scheduled check, or a click, tries again.
    internal void ShowNotUpdated(string reason)
    {
        ShowBalloon(
            "Weir Update",
            $"Weir didn't update because it couldn't save a copy of its data: {reason}",
            ToolTipIcon.Warning,
            () => _opener.Open("tray-update-not-applied", Program.UpdateCheckPath));
        Render();
    }

    // The server is stopped through ServerProcessStop before anything else happens, so nothing that follows, an
    // update install included, can cut a job short (#857).
    private async Task StopServerAsync()
    {
        await _cts.CancelAsync();
        await _server.StopAsync();
        _shell.Hide();
    }

    // -- Tray icon ----------------------------------------------------------

    private void CreateTrayIcon()
    {
        _shell.Open(new TrayShellHandlers(
            MenuClicks(),
            Render,
            _iconClicks.OnMouseClick,
            _iconClicks.OnDoubleClick,
            _balloons.OnClicked,
            OnBlinkTick));
        _opened = true;
        Render();
    }

    internal Dictionary<TrayMenuItem, Action> MenuClicks() => new()
    {
        [TrayMenuItem.Open] = () => _opener.OpenFromClick("tray"),
        [TrayMenuItem.Pause] = _pause.Toggle,
        [TrayMenuItem.Restart] = RestartObserved,
        [TrayMenuItem.CopyAddress] = CopyAddress,
        [TrayMenuItem.AllowOtherDevices] = () => _lanAccessMenu.Allow(),
        [TrayMenuItem.OnlyThisPc] = () => _lanAccessMenu.LimitToThisPc(),
        [TrayMenuItem.ChangePort] = () => BackgroundWork.Observe("Change port", _portChange.RunAsync()),
        [TrayMenuItem.OpenDataFolder] = () => _shell.OpenFolder(_runtimeHome, "data folder"),
        [TrayMenuItem.OpenLogsFolder] = () => _shell.OpenFolder(LogsFolder(), "logs folder"),
        [TrayMenuItem.StartWithWindows] = () =>
        {
            _startup.Toggle();
            Render();
        },
        [TrayMenuItem.Update] = OnUpdateMenuClick,
        [TrayMenuItem.ReportProblem] = () => ProblemReport.Open(_shell, LogsFolder()),
        [TrayMenuItem.Quit] = () => BackgroundWork.Observe("Quit", QuitAsync()),
    };

    private void ShowBalloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        if (!_opened)
        {
            return;
        }
        _balloons.Show(title, text, icon, onClick);
    }

    private string LogsFolder() => RuntimeFolders.Logs(_runtimeHome, Environment.GetEnvironmentVariable);

    // The file a balloon about a failure names.
    private void ShowTrayLog() => _shell.RevealFile(Path.Combine(_runtimeHome, TrayLog.FileName));

    internal async Task QuitAsync()
    {
        TrayLog.Write("Quit requested from tray icon");
        if (!await _shutdown.QuitAsync())
        {
            // An update is being applied, and ends this process by itself.
            return;
        }
        _shell.Exit();
    }

    // -- Restart, address -------------------------------------------------------

    private void RestartObserved() => BackgroundWork.Observe("Restart Weir", RestartAsync());

    internal async Task RestartAsync()
    {
        TrayLog.Write("Restart Weir chosen.");
        try
        {
            if (await _server.RestartNowAsync(_cts.Token))
            {
                BeginWatching();
            }
            else
            {
                ShowBalloon("Weir", CouldNotStartText(), ToolTipIcon.Error, RestartObserved);
            }
        }
        catch (OperationCanceledException)
        {
            TrayLog.Write("Restart Weir: stopped, because Weir is quitting or updating.");
        }
    }

    private void CopyAddress()
    {
        var address = TrayState.AddressToCopy(_server.Scope, _server.Port, Environment.MachineName);
        try
        {
            _shell.CopyToClipboard(address);
            TrayLog.Write($"Copied {address} to the clipboard.");
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            TrayLog.Write($"Could not copy {address} to the clipboard: {ex.Message}");
        }
    }

    // A second launch signalled: this copy is the one running.
    internal void ShowAlreadyRunning()
    {
        TrayLog.Write("Weir was started again; telling the person it is already running.");
        _opener.ShowAlreadyRunning();
    }

    // The notices that point to tray-host.log show that file, selected.
    private void ShowLanAccessNotice(LanAccessNotice notice) =>
        ShowBalloon(
            "Weir",
            notice.Text,
            notice.IsWarning ? ToolTipIcon.Warning : ToolTipIcon.Info,
            notice.NamesLog ? ShowTrayLog : null);

    public void Dispose()
    {
        _statusWatcher?.Dispose();
        _lanAccess.Dispose();
    }
}
