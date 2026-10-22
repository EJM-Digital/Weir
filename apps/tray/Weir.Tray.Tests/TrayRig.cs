using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Weir.Tray.LanAccess;
using Weir.Tray.Tests.LanAccess;

namespace Weir.Tray.Tests;

/// <summary>The bundled server as the controller sees it: every answer is set by the test, and every call is recorded.</summary>
internal sealed class FakeTrayServer : ITrayServer
{
    public event Action? PhaseChanged;

    public int Port { get; set; } = 9347;

    public ServerPhase Phase { get; private set; } = ServerPhase.Starting;

    public DateTime? StartedAtUtc { get; set; }

    public RunningServer? Running { get; set; }

    public bool ServerIsRunning { get; set; }

    public StartupError? StartupError { get; set; }

    public StartupError? GaveUpError { get; set; }

    public int GaveUpAfterAttempts { get; set; }

    public int GaveUpPortInUse { get; set; }

    public bool NoPortChosen { get; set; }

    public ListenScope Scope { get; set; } = ListenScope.ThisPcOnly;

    public Func<CancellationToken, Task<PortChoice.Resolution>>? ResolvePort { get; set; }

    /// <summary>What starting the server does; it ends the start when it returns or throws.</summary>
    public Func<Task> OnStart { get; set; } = () => Task.CompletedTask;

    public Func<Task> OnStop { get; set; } = () => Task.CompletedTask;

    public Func<CancellationToken, Task<bool>> OnRestart { get; set; } = _ => Task.FromResult(true);

    public Func<int, Task<PortMove>> OnMove { get; set; } = _ => Task.FromResult(PortMove.Moved);

    public List<string> Calls { get; } = [];

    public Action? GaveUp { get; private set; }

    public void SetPhase(ServerPhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke();
    }

    public void PrepareEnvironment() => Calls.Add("prepare");

    public void WritePortFile() => Calls.Add("write-port-file");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");
        return OnStart();
    }

    public Task<bool> RestartNowAsync(CancellationToken cancellationToken)
    {
        Calls.Add("restart");
        return OnRestart(cancellationToken);
    }

    public void Watch(Action onGaveUp, CancellationToken cancellationToken)
    {
        Calls.Add("watch");
        GaveUp = onGaveUp;
    }

    public Task StopAsync()
    {
        Calls.Add("stop");
        return OnStop();
    }

    public Task<PortMove> MoveToPortAsync(int to, CancellationToken cancellationToken)
    {
        Calls.Add($"move-to-{to}");
        return OnMove(to);
    }

    public Task<ScopeChange> MoveToScopeAsync(ListenScope to, CancellationToken cancellationToken)
    {
        Calls.Add($"move-to-scope-{to}");
        Scope = to;
        return Task.FromResult(ScopeChange.Applied);
    }
}

/// <summary>Windows as the controller sees it: everything it is asked to show is recorded, and what runs "on the UI thread" waits for <see cref="Drain"/>.</summary>
internal sealed class FakeTrayShell : ITrayShell
{
    private readonly ConcurrentQueue<Action> _posted = new();

    public TrayShellHandlers? Handlers { get; private set; }

    public List<TrayIconKey> Icons { get; } = [];

    public List<string> HoverTexts { get; } = [];

    public List<IReadOnlyList<TrayMenuEntry>> Menus { get; } = [];

    public bool Blinking { get; private set; }

    public List<(string Title, string Text, ToolTipIcon Icon, int TimeoutMs)> Balloons { get; } = [];

    /// <summary>Raised after each balloon and each hand-over to the UI thread, so a test can wait for what the controller does from background work.</summary>
    public TestActivity Activity { get; set; } = new();

    public bool Hidden { get; private set; }

    public List<string> Clipboard { get; } = [];

    /// <summary>Thrown by the clipboard when set.</summary>
    public Exception? ClipboardFailure { get; set; }

    public List<(int Port, string Path)> BrowserOpens { get; } = [];

    public bool BrowserOpens_Works { get; set; } = true;

    public List<(string Folder, string What)> Folders { get; } = [];

    public List<string> Revealed { get; } = [];

    public int? PortAnswer { get; set; }

    public List<PortPrompt> PortQuestions { get; } = [];

    public bool StartWithWindowsAnswer { get; set; }

    public int StartWithWindowsAsked { get; private set; }

    public int ThreadExits { get; private set; }

    public int Exits { get; private set; }

    public TrayIconKey LastIcon => Icons[^1];

    public string LastHoverText => HoverTexts[^1];

    public IReadOnlyList<TrayMenuEntry> LastMenu => Menus[^1];

    public (string Title, string Text, ToolTipIcon Icon, int TimeoutMs) LastBalloon => Balloons[^1];

    /// <summary>Runs what was handed to the UI thread, and whatever that hands on, until nothing is left.</summary>
    public void Drain()
    {
        while (_posted.TryDequeue(out var action))
        {
            action();
        }
    }

    public void Open(TrayShellHandlers handlers) => Handlers = handlers;

    public void Post(Action action)
    {
        _posted.Enqueue(action);
        Activity.Raise();
    }

    public void ShowIcon(TrayIconKey key) => Icons.Add(key);

    public void ShowHoverText(string text) => HoverTexts.Add(text);

    public void ShowMenu(IReadOnlyList<TrayMenuEntry> entries) => Menus.Add(entries);

    public void SetBlinking(bool on) => Blinking = on;

    public void ShowBalloon(string title, string text, ToolTipIcon icon, int timeoutMs)
    {
        Balloons.Add((title, text, icon, timeoutMs));
        Activity.Raise();
    }

    public void Hide() => Hidden = true;

    public void CopyToClipboard(string text)
    {
        if (ClipboardFailure is not null)
        {
            throw ClipboardFailure;
        }
        Clipboard.Add(text);
    }

    public bool OpenBrowser(int port, string relativePath)
    {
        BrowserOpens.Add((port, relativePath));
        return BrowserOpens_Works;
    }

    public void OpenFolder(string folder, string what) => Folders.Add((folder, what));

    public void RevealFile(string file) => Revealed.Add(file);

    public int? AskPort(PortPrompt prompt)
    {
        PortQuestions.Add(prompt);
        return PortAnswer;
    }

    public bool AskStartWithWindows()
    {
        StartWithWindowsAsked++;
        return StartWithWindowsAnswer;
    }

    public void ExitThread() => ThreadExits++;

    public void Exit() => Exits++;

    public void Click(TrayMenuItem item) => Handlers!.MenuClicks[item]();
}

/// <summary>
/// A tray controller with a fake server and a fake shell, in a throw-away runtime home, on a clock the test moves. Nothing
/// leaves the process: no icon, no balloon, no browser, no server.
/// </summary>
internal sealed class TrayRig : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempRegistryKey _run = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly TempRegistryKey _answer = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly TempDirectory _startupFolder = TempDirectory.Create();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SecondLaunchSignal _secondLaunch = new($@"Local\WeirTrayTests-{Guid.NewGuid():n}");

    internal TrayRig(
        TrayStart? start = null,
        FakeUpdateService? updates = null,
        UpdateSettings? settings = null,
        Func<PortChoice.Resolution>? resolvePort = null)
    {
        Updates = updates ?? new FakeUpdateService { IsInstalled = false };
        Updates.Activity = Activity;
        Shell.Activity = Activity;
        Startup = new StartupRegistration(_run.Path, new StartWithWindowsAnswer(_answer.Path), _startupFolder.Path, @"C:\Weir\Weir.exe");
        Controller = new TrayController(
            _home.Path,
            start ?? new TrayStart(AnnounceOnReady: true, Interactive: true),
            Server,
            Shell,
            Firewall,
            Startup,
            Updates,
            settings ?? new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = 0 },
            _secondLaunch,
            Clock,
            _shutdown,
            resolvePort);
    }

    internal string Home => _home.Path;

    /// <summary>Raised by the shell and the update service whenever the controller changes either, so a test waits for a change and not for the clock.</summary>
    internal TestActivity Activity { get; } = new();

    internal FakeTrayServer Server { get; } = new();

    internal FakeTrayShell Shell { get; } = new();

    internal FakeFirewall Firewall { get; } = new() { Allows = true };

    internal FakeTimeProvider Clock { get; } = new();

    internal FakeUpdateService Updates { get; }

    internal StartupRegistration Startup { get; }

    internal TrayController Controller { get; }

    internal CancellationToken Shutdown => _shutdown.Token;

    internal void Click(TrayMenuItem item) => Shell.Click(item);

    public void Dispose()
    {
        _shutdown.Cancel();
        Controller.Dispose();
        _secondLaunch.Dispose();
        _shutdown.Dispose();
        _startupFolder.Dispose();
        _answer.Dispose();
        _run.Dispose();
        _home.Dispose();
    }
}
