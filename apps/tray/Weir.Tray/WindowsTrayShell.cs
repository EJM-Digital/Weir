using System.ComponentModel;

namespace Weir.Tray;

/// <summary>
/// The tray's notification-area icon, its context menu, its blink timer and the rest of what the tray does to Windows, as
/// <see cref="ITrayShell"/>. It decides nothing: every click is handed to <see cref="TrayShellHandlers"/>, and everything
/// shown is what the controller asked to be shown. The calls into Windows that put a window up or start a program go
/// through <see cref="ShellEffects"/>.
/// </summary>
sealed class WindowsTrayShell(ShellEffects effects) : ITrayShell, IDisposable
{
    private SynchronizationContext? _ui;
    private TrayShellHandlers? _handlers;
    private NotifyIcon? _notifyIcon;
    private TrayIcons? _icons;
    private System.Windows.Forms.Timer? _blinkTimer;
    private TrayMenuView? _menu;

    internal NotifyIcon? NotifyIcon => _notifyIcon;

    internal System.Windows.Forms.Timer? BlinkTimer => _blinkTimer;

    internal TrayMenuView? Menu => _menu;

    /// <summary>
    /// Makes the calling thread the UI thread: what <see cref="Post"/> is given runs on it, in its message loop. Call this
    /// on the thread that runs <see cref="Run"/>, before anything else.
    /// </summary>
    internal void AttachToThisThread()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Runs the message loop until the tray ends.</summary>
    internal void Run() => effects.RunMessageLoop();

    public void Post(Action action) => _ui!.Post(_ => action(), null);

    public void Open(TrayShellHandlers handlers)
    {
        _handlers = handlers;
        _icons = new TrayIcons(size => Program.LoadAppIcon(size), SystemInformation.SmallIconSize);
        _blinkTimer = new System.Windows.Forms.Timer { Interval = (int)TrayBlink.Interval.TotalMilliseconds };
        _blinkTimer.Tick += OnBlinkTimer;
        _menu = new TrayMenuView(handlers.MenuClicks, staysOpen: [TrayMenuItem.StartWithWindows]);
        _menu.Strip.Opening += OnMenuOpening;

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu.Strip };
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.DoubleClick += OnDoubleClick;
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        _notifyIcon.Icon = _icons.For(new TrayIconKey(TrayDot.Starting, TrayMark.None));
        _notifyIcon.Visible = true;
    }

    public void ShowIcon(TrayIconKey key) => _notifyIcon!.Icon = _icons!.For(key);

    public void ShowHoverText(string text) => _notifyIcon!.Text = text;

    public void ShowMenu(IReadOnlyList<TrayMenuEntry> entries) => _menu!.Show(entries);

    public void SetBlinking(bool on) => _blinkTimer!.Enabled = on;

    public void ShowBalloon(string title, string text, ToolTipIcon icon, int timeoutMs) =>
        _notifyIcon!.ShowBalloonTip(timeoutMs, title, text, icon);

    public void Hide() => _notifyIcon!.Visible = false;

    public void CopyToClipboard(string text) => effects.SetClipboardText(text);

    public bool OpenBrowser(int port, string relativePath) => Program.OpenBrowser(port, effects.StartProcess, relativePath);

    public void OpenFolder(string folder, string what) => RuntimeFolders.Open(folder, what, effects.StartProcess);

    public void RevealFile(string file) => RuntimeFolders.Reveal(file, effects.StartProcess);

    public int? AskPort(PortPrompt prompt) => effects.AskPort(prompt, _notifyIcon?.Icon);

    public bool AskStartWithWindows() => effects.AskStartWithWindows();

    public void ExitThread() => effects.ExitThread();

    public void Exit() => effects.ExitApplication();

    public void Dispose()
    {
        _blinkTimer?.Dispose();
        _notifyIcon?.Dispose();
        _menu?.Dispose();
        _icons?.Dispose();
    }

    // What Windows Forms raises, as plain methods so that what each one does can be run without a person at the icon.

    internal void OnBlinkTimer(object? sender, EventArgs e) => _handlers!.BlinkTick();

    internal void OnMenuOpening(object? sender, CancelEventArgs e) => _handlers!.MenuOpening();

    // Clicking the icon is a person asking for Weir, so one click opens it (#638). A double-click still opens
    // one window: its second event falls inside the debounce.
    internal void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _handlers!.IconClicked();
        }
    }

    internal void OnDoubleClick(object? sender, EventArgs e) => _handlers!.IconDoubleClicked();

    internal void OnBalloonTipClicked(object? sender, EventArgs e) => _handlers!.BalloonClicked();
}
