using System.ComponentModel;
using System.Runtime.InteropServices;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>How this run was started.</summary>
/// <param name="AnnounceOnReady">Whether a balloon tells the person Weir is running once its server is ready.</param>
/// <param name="Interactive">Whether there is a person to tell when the server cannot start. A start with no one to tell exits instead.</param>
readonly record struct TrayStart(bool AnnounceOnReady, bool Interactive)
{
    /// <summary>Whether the person started Weir themselves: not silent, not at sign-in or by another program (both pass --no-browser), and with a desktop.</summary>
    internal bool StartedByPerson => AnnounceOnReady && Interactive;
}

/// <summary>
/// The tray icon and its menu. Everything that touches them runs on the UI thread: work on other threads (the
/// server watchdog, update checks, a port change) hands its result back through <see cref="OnUi"/>. The icon, its hover
/// text and the menu are all drawn from one place, <see cref="Render"/>, from the state the tray keeps, so they cannot
/// disagree.
/// </summary>
sealed class TrayApp : IDisposable
{
    private static readonly TimeSpan BrowserDebounceWindow = TimeSpan.FromMilliseconds(1250);

    private readonly string _runtimeHome;
    private readonly TrayStart _start;
    private readonly ServerHost _server;
    private readonly IFirewallAccess _firewall = new WindowsFirewallAccess();
    private readonly LanAccessSync _lanAccess;
    private readonly UpdateSettings _updateSettings;
    private readonly IUpdateService _updateService;
    private readonly TrayShutdown _shutdown;
    private readonly SecondLaunchSignal _secondLaunch;
    private readonly StartupRegistration _startup = StartupRegistration.ForThisUser();
    private readonly TrayStatusGate _serverStatus;
    private readonly TrayBlink _blink = new();
    private readonly TrayStatusDeadline _statusDeadline = new(TimeProvider.System.GetUtcNow);
    private readonly WeirOpener _opener;
    private readonly PauseControl _pause;
    private readonly PortChange _portChange;
    private readonly CancellationTokenSource _cts = new();

    private SynchronizationContext? _ui;
    private NotifyIcon? _notifyIcon;
    private TrayIcons? _icons;
    private System.Windows.Forms.Timer? _blinkTimer;
    private TrayMenuView? _menu;
    private TrayStatusWatcher? _statusWatcher;
    private TrayUpdates? _updates;
    private LanAccessMenu? _lanAccessMenu;
    private bool _watching;
    private BalloonPresenter? _balloons;
    private int _exitCode;

    public TrayApp(
        int port,
        ListenScope listenScope,
        TrayStart start,
        IUpdateService updateService,
        UpdateSettings updateSettings,
        SecondLaunchSignal secondLaunch,
        Func<PortChoice.Resolution>? resolvePort = null)
    {
        var installRoot = AppContext.BaseDirectory;
        _runtimeHome = Program.RuntimeHome();
        _start = start;
        _secondLaunch = secondLaunch;
        _server = new ServerHost(_runtimeHome, installRoot, port, listenScope);
        if (resolvePort is not null)
        {
            // The port was not known when the tray began (port.txt was busy): each start attempt asks again, on the UI thread, where a
            // dialog can be shown.
            _server.ResolvePort = _ => ResolveOnUiAsync(resolvePort);
        }
        _lanAccess = new LanAccessSync(_runtimeHome, _server, _firewall, TimeProvider.System);
        _updateSettings = updateSettings;
        _updateService = updateService;
        _shutdown = new TrayShutdown(
            StopServerAsync,
            updateService,
            new UpdateBackupHook(
                new UpdateBackupRequest(
                    _runtimeHome,
                    TimeProvider.System,
                    () => _server.Running,
                    message => OnUi(() => ShowBalloon("Weir Update", message, ToolTipIcon.Info))).AskAsync,
                _runtimeHome,
                () => _server.ServerIsRunning,
                reason => OnUi(() => ShowNotUpdated(reason))));
        _serverStatus = new TrayStatusGate(() => _server.StartedAtUtc);
        _opener = new WeirOpener(
            () => _server.Phase,
            () => _server.Port,
            new Debounce(BrowserDebounceWindow, TimeProvider.System),
            (openPort, path) => Program.OpenBrowser(openPort, relativePath: path),
            ShowBalloon,
            RestartObserved);
        _pause = new PauseControl(_runtimeHome, TimeProvider.System, () => State().IsPaused, ShowBalloon, ShowTrayLog);
        _portChange = new PortChange(
            () => _server.Port,
            prompt => PortDialog.Ask(prompt, PortChoice.IsInUse, _notifyIcon?.Icon),
            _server.MoveToPortAsync,
            ShowBalloon,
            () => _opener.Open("port-change-balloon"),
            ShowTrayLog,
            Render,
            _cts.Token);

        TrayLog.Write($"Starting tray host. installRoot={installRoot} runtimeHome={_runtimeHome}");
    }

    /// <summary>Starts the server and runs the tray until Quit or an update restart. Returns the process exit code.</summary>
    public int Run()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        _ui = SynchronizationContext.Current;

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
        TrayLog.Write("Starting tray icon event loop");
        Application.Run();
        TrayLog.Write("Tray icon event loop ended.");
        return _exitCode;
    }

    private async Task StartAsync()
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
                _exitCode = 1;
                Application.ExitThread();
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
                    () => OnUi(() => _lanAccessMenu!.OnWaitingForWindows()),
                    applied => OnUi(() => _lanAccessMenu!.OnChangedElsewhere(applied))),
                ct),
            _cts.Token);

        _updates!.Start();
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
    private void OnUi(Action action) => _ui!.Post(_ => action(), null);

    // -- What the tray shows ------------------------------------------------

    private TrayState State()
    {
        var phase = _server.Phase;
        var status = _serverStatus.Current;
        _statusDeadline.Follow(waiting: phase == ServerPhase.Running && status is null);
        return new TrayState(
            phase,
            status,
            _updates is { IsDownloaded: true } updates ? updates.PendingVersion : null,
            _server.Port,
            _statusDeadline.Overdue,
            _server.StartupError ?? _server.GaveUpError);
    }

    // The server's own reason when it said one (a copy of the data it could not save before updating), else the general text.
    private string CouldNotStartText() =>
        _server.StartupError is { } why ? TrayBalloons.CouldNotStartBecause(why)
        : _server.NoPortChosen ? TrayBalloons.NoPortChosenText
        : _server.GaveUpPortInUse is > 0 and var held ? TrayBalloons.PortInUseText(held, _server.GaveUpAfterAttempts)
        : _server.GaveUpAfterAttempts is > 0 and var tries ? TrayBalloons.GaveUpText(tries)
        : TrayBalloons.CouldNotStartText;

    // The same, for a server that ran and then could not be brought back.
    private string StoppedText() =>
        _server.StartupError is { } why ? TrayBalloons.StoppedBecause(why)
        : _server.GaveUpPortInUse is > 0 and var held ? TrayBalloons.StoppedPortInUseText(held, _server.GaveUpAfterAttempts)
        : _server.GaveUpAfterAttempts is > 0 and var tries ? TrayBalloons.StoppedAfterTriesText(tries)
        : TrayBalloons.StoppedText;

    private void AskAboutStartWithWindows()
    {
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(_start.StartedByPerson, StartWithWindowsPrompt.AskInMessageBox, _startup);
        Render();
    }

    // The icon's dot and mark, the hover text and the menu, from the state as it is now.
    private void Render()
    {
        if (_notifyIcon is null || _menu is null || _updates is null || _lanAccessMenu is null)
        {
            return;
        }
        var state = State();
        _blink.Follow(state.Dot);
        _blinkTimer!.Enabled = _blink.Active;
        ShowIcon(state);
        _notifyIcon.Text = state.HoverText;
        _menu.Show(TrayMenu.Describe(new TrayMenuInputs(
            state,
            _updates.MenuState(),
            _lanAccessMenu.State,
            _portChange.MovingTo,
            _startup.IsEnabled,
            AppVersion.Current)));
    }

    // Only the icon: the blink swaps between icons already made, and leaves the hover text and the menu alone.
    private void ShowIcon(TrayState state) => _notifyIcon!.Icon = _icons!.For(state.IconKey(_blink.Lit));

    // A server that is starting has said nothing yet: what the one before it left in tray-status.json is not shown.
    private void ShowPhase(ServerPhase phase)
    {
        if (phase == ServerPhase.Starting)
        {
            _serverStatus.ServerStarting();
        }
        Render();
    }

    private void ShowServerStatus(TrayStatusReading reading)
    {
        _serverStatus.Offer(reading);
        Render();
    }

    // -- Updates ------------------------------------------------------------

    private void ShowUpdateNotice(UpdateMode mode)
    {
        var notice = UpdateNotices.For(
            mode,
            _updates?.PendingVersion ?? "new version",
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
        switch (_updates?.MenuState().Action)
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

    private Task ApplyUpdateAndRestartAsync() =>
        new UpdateRestart(() => _updates is { IsDownloaded: true }, _shutdown.RestartToUpdateAsync, ShowBalloon, TrayLog.Write).RunAsync();

    // The update stays downloaded and the old version keeps running; the next scheduled check, or a click, tries again.
    private void ShowNotUpdated(string reason)
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
        _notifyIcon!.Visible = false;
    }

    // -- Tray icon ----------------------------------------------------------

    private void CreateTrayIcon()
    {
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
            TimeProvider.System,
            _cts.Token,
            () => UpdateSettings.Load(_runtimeHome));
        _icons = new TrayIcons(size => Program.LoadAppIcon(size), SystemInformation.SmallIconSize);
        _blinkTimer = new System.Windows.Forms.Timer { Interval = (int)TrayBlink.Interval.TotalMilliseconds };
        _blinkTimer.Tick += (_, _) =>
        {
            // A status that has not come in time ends the blink, and that changes more than the icon.
            var state = State();
            if (state.Dot != TrayDot.Starting)
            {
                Render();
            }
            else if (_blink.Tick())
            {
                ShowIcon(state);
            }
        };
        _menu = new TrayMenuView(MenuClicks(), staysOpen: [TrayMenuItem.StartWithWindows]);
        _menu.Strip.Opening += (_, _) => Render();

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu.Strip };

        var clicks = new TrayIconClicks(_opener);
        _notifyIcon.MouseClick += (_, e) => clicks.OnMouseClick(e.Button);
        _notifyIcon.DoubleClick += (_, _) => clicks.OnDoubleClick();

        // One handler for every balloon: a click does what the balloon on screen offered, and nothing for a balloon
        // that offered nothing.
        _balloons = new BalloonPresenter(new NotifyIconBalloons(_notifyIcon), TrayLog.Write);
        _notifyIcon.BalloonTipClicked += (_, _) => _balloons.OnClicked();

        Render();
        _notifyIcon.Visible = true;
    }

    private Dictionary<TrayMenuItem, Action> MenuClicks() => new()
    {
        [TrayMenuItem.Open] = () => _opener.OpenFromClick("tray"),
        [TrayMenuItem.Pause] = _pause.Toggle,
        [TrayMenuItem.Restart] = RestartObserved,
        [TrayMenuItem.CopyAddress] = CopyAddress,
        [TrayMenuItem.AllowOtherDevices] = () => _lanAccessMenu!.Allow(),
        [TrayMenuItem.OnlyThisPc] = () => _lanAccessMenu!.LimitToThisPc(),
        [TrayMenuItem.ChangePort] = () => BackgroundWork.Observe("Change port", _portChange.RunAsync()),
        [TrayMenuItem.OpenDataFolder] = () => RuntimeFolders.Open(_runtimeHome, "data folder"),
        [TrayMenuItem.OpenLogsFolder] = () => RuntimeFolders.Open(LogsFolder(), "logs folder"),
        [TrayMenuItem.StartWithWindows] = () =>
        {
            _startup.Toggle();
            Render();
        },
        [TrayMenuItem.Update] = OnUpdateMenuClick,
        [TrayMenuItem.ReportProblem] = () => ProblemReport.Open(LogsFolder()),
        [TrayMenuItem.Quit] = () => BackgroundWork.Observe("Quit", QuitAsync()),
    };

    private void ShowBalloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloons?.Show(title, text, icon, onClick);
    }

    private string LogsFolder() => RuntimeFolders.Logs(_runtimeHome, Environment.GetEnvironmentVariable);

    // The file a balloon about a failure names.
    private void ShowTrayLog() => RuntimeFolders.Reveal(Path.Combine(_runtimeHome, TrayLog.FileName));

    private async Task QuitAsync()
    {
        TrayLog.Write("Quit requested from tray icon");
        if (!await _shutdown.QuitAsync())
        {
            // An update is being applied, and ends this process by itself.
            return;
        }
        Application.Exit();
    }

    // -- Restart, address -------------------------------------------------------

    private void RestartObserved() => BackgroundWork.Observe("Restart Weir", RestartAsync());

    private async Task RestartAsync()
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
            Clipboard.SetText(address);
            TrayLog.Write($"Copied {address} to the clipboard.");
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            TrayLog.Write($"Could not copy {address} to the clipboard: {ex.Message}");
        }
    }

    // A second launch signalled: this copy is the one running.
    private void ShowAlreadyRunning()
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
        _cts.Cancel();
        _statusWatcher?.Dispose();
        _server.Dispose();
        _blinkTimer?.Dispose();
        _notifyIcon?.Dispose();
        _menu?.Dispose();
        _icons?.Dispose();
        _lanAccess.Dispose();
        _cts.Dispose();
    }
}
