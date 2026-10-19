namespace Weir.Tray;

/// <summary>
/// When the tray checks for, downloads and applies updates, following update-settings.json, and when the server asks
/// for one of those steps now (a person pressed a button in System › About). It runs off the UI thread and reports
/// through callbacks that the tray runs on its UI thread, and through update-state.json, which it rewrites at each
/// step. It also tells the server it is alive, so a page never offers a button nobody is there to answer. In Automatic mode a downloaded update is applied once Weir has been idle for a while (#875); in the other
/// modes only a person, or a quit or start, installs it.
/// </summary>
sealed class TrayUpdates
{
    private const string CheckNowFlagFileName = "update-check-now";
    private const string DownloadNowFlagFileName = "update-download-now";
    private const string ApplyNowFlagFileName = "update-apply-now";
    private const string NoNewerVersion = "There is no newer version of Weir to download.";
    private const string DownloadFirst = "Download the update first.";

    /// <summary>How often the server is told the tray is alive; the server counts it alive for three times as long.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old a request flag may be and still be somebody's click. An older one was left by a tray that was not there to take
    /// it, and acting on it now (a download in Notify-only mode, a restart) would surprise the person who left it.
    /// </summary>
    internal static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    private readonly string _runtimeHome;
    private readonly UpdateSettings _settings;
    private readonly IUpdateService _service;
    private readonly UpdateCallbacks _callbacks;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _shutdown;
    private readonly Lock _idleWatchLock = new();
    private readonly Lock _requestsLock = new();
    private readonly Lock _stateLock = new();
    private int _activity;
    private Task? _idleWatch;
    private Task? _requestedWork;
    private (UpdatePhase Phase, string? Version, string? Failure) _lastState = (UpdatePhase.Idle, null, null);

    /// <param name="service">Velopack: checking for updates and downloading them. A download installs nothing.</param>
    /// <param name="runtimeHome">Where update-state.json, the request flags and the server's work-state.json live.</param>
    /// <param name="settings">The operator's update choices.</param>
    /// <param name="callbacks">How the rest of the tray hears about updates and applies them.</param>
    /// <param name="clock">Times the wait for Weir to be idle and the log of a failing watcher.</param>
    /// <param name="shutdown">Cancelled when the tray is ending, which ends every loop here.</param>
    internal TrayUpdates(
        IUpdateService service,
        string runtimeHome,
        UpdateSettings settings,
        UpdateCallbacks callbacks,
        TimeProvider clock,
        CancellationToken shutdown)
    {
        _service = service;
        _runtimeHome = runtimeHome;
        _settings = settings;
        _callbacks = callbacks;
        _clock = clock;
        _shutdown = shutdown;
    }

    internal bool IsDownloaded => _service.IsDownloaded;

    internal string? PendingVersion => _service.PendingVersion;

    /// <summary>
    /// The wait for Weir to be idle so that a downloaded update can install itself, or null when none was started. It
    /// is complete once the update has been handed over for installing or the wait has ended without one.
    /// </summary>
    internal Task? IdleWatch
    {
        get
        {
            lock (_idleWatchLock)
            {
                return _idleWatch;
            }
        }
    }

    /// <summary>The check or download the last request started, or null when none did. A test awaits it.</summary>
    internal Task? RequestedWork => Volatile.Read(ref _requestedWork);

    internal UpdateMenuState MenuState() =>
        UpdateMenuState.Describe(
            _service.IsInstalled,
            (UpdateActivity)Volatile.Read(ref _activity),
            _service.HasPendingUpdate,
            _service.IsDownloaded,
            _service.PendingVersion);

    /// <summary>Starts the start-up check, the periodic check and the watch for requests, as the settings say.</summary>
    internal void Start()
    {
        if (!_service.IsInstalled)
        {
            // It still says it is alive, so System › About can tell a person this copy cannot update itself rather than that no tray is running.
            TrayLog.Write("Velopack: not installed (portable or development copy), skipping update checks.");
            WriteHeartbeat();
            BackgroundWork.RunLoop("Update heartbeat", BeatAsync, _shutdown);
            return;
        }

        // The server offers its buttons only while this is fresh, so it is written before the state that makes the page look.
        WriteHeartbeat();

        // This process has downloaded nothing yet. A file left by the run before it (one that downloaded the update now
        // installed) would otherwise keep telling the server an update is waiting until a check next finishes.
        WriteState(UpdatePhase.Idle, null);

        WatchForRequests();

        if (_settings.CheckOnStartup)
        {
            CheckInBackground();
        }

        if (_settings.CheckIntervalMinutes > 0)
        {
            var interval = TimeSpan.FromMinutes(_settings.CheckIntervalMinutes);
            BackgroundWork.RunLoop("Periodic update check", ct => CheckPeriodicallyAsync(interval, ct), _shutdown);
        }

        BackgroundWork.RunLoop("Update heartbeat", BeatAsync, _shutdown);
    }

    // A watcher that cannot be built must not take the start-up and periodic checks down with it: the requests from System
    // are then unanswered, which the log says, and everything else goes on. Its first look is the one at the flags a person
    // left before this run.
    private void WatchForRequests()
    {
        try
        {
            var requests = new UpdateRequestWatcher(
                _runtimeHome,
                () => BackgroundWork.Forget("Update requests", () =>
                {
                    ActOnRequests();
                    return Task.CompletedTask;
                }),
                TrayLog.Write,
                _clock);
            _shutdown.Register(requests.Dispose);
            requests.Start();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            TrayLog.Write($"The tray could not watch for update requests from System ({ex.Message}); its buttons will not be answered until Weir is restarted.");
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(HeartbeatInterval, _clock, cancellationToken).ConfigureAwait(false);
            WriteHeartbeat();
        }
    }

    private void WriteHeartbeat()
    {
        try
        {
            TrayHeartbeatFile.Write(_runtimeHome, _clock.GetUtcNow(), _service.IsInstalled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not write the tray's heartbeat: {ex.Message}");
        }
    }

    internal void CheckInBackground() => _ = StartInBackground("Update check", () => CheckAsync());

    internal void DownloadInBackground() => _ = StartInBackground("Update download", () => DownloadAsync());

    private static Task StartInBackground(string name, Func<Task> work)
    {
        var started = Task.Run(work);
        BackgroundWork.Observe(name, started);
        return started;
    }

    private async Task CheckPeriodicallyAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await CheckAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Does what the flag files in the runtime home ask for, once each: System › About asks for a step by creating one
    /// (check now, download now, or restart and apply). Every flag is taken away, so none is left for a later look to act on.
    /// One older than <see cref="RequestMaxAge"/> is thrown away unanswered. A step that cannot be taken is answered by
    /// writing the state as it truly is, so the page hears of it at once.
    /// </summary>
    internal void ActOnRequests()
    {
        lock (_requestsLock)
        {
            if (Take(ApplyNowFlagFileName))
            {
                if (_service.IsDownloaded)
                {
                    TrayLog.Write("Apply-now flag detected - applying update and restarting.");
                    _callbacks.OnUi(_callbacks.ApplyNow);
                }
                else
                {
                    AnswerApplyBeforeDownload();
                }
            }
            if (Take(CheckNowFlagFileName))
            {
                TrayLog.Write("Check-now flag detected - checking for an update.");
                Volatile.Write(ref _requestedWork, StartInBackground("Update check", () => CheckAsync(asked: true)));
            }
            if (Take(DownloadNowFlagFileName))
            {
                TrayLog.Write("Download-now flag detected - downloading the update.");
                Volatile.Write(ref _requestedWork, StartInBackground("Update download", () => DownloadAsync(asked: true)));
            }
        }
    }

    // Takes the flag away and says whether it was a request to act on: present and not left by a tray long gone.
    private bool Take(string flagFileName)
    {
        var flagPath = Path.Combine(_runtimeHome, flagFileName);
        if (!File.Exists(flagPath))
        {
            return false;
        }
        var age = _clock.GetUtcNow() - File.GetLastWriteTimeUtc(flagPath);
        try
        {
            File.Delete(flagPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not delete {flagPath}: {ex.Message}");
        }
        if (age > RequestMaxAge)
        {
            TrayLog.Write($"Ignoring {flagFileName}: it was left {age.TotalMinutes:0} minutes ago, so nobody is waiting on it.");
            return false;
        }
        return true;
    }

    // The page offered the restart, but this tray has no downloaded update (it was restarted, or the download did not finish).
    // While a step is under way the state already says so; otherwise the page is told what to do.
    private void AnswerApplyBeforeDownload()
    {
        if (Volatile.Read(ref _activity) != (int)UpdateActivity.Idle)
        {
            RepublishState();
            return;
        }
        TrayLog.Write("Apply-now flag detected, but no update is downloaded.");
        WriteState(UpdatePhase.Failed, PendingVersion, DownloadFirst);
    }

    // Automatic mode only: the person who chose it is not there to press Restart to update.
    private void InstallWhenIdle()
    {
        lock (_idleWatchLock)
        {
            if (_idleWatch is { IsCompleted: false })
            {
                return;
            }
            TrayLog.Write($"Update v{PendingVersion} downloaded; installing once Weir has been idle for {IdleInstall.IdlePeriodText}.");
            _idleWatch = BackgroundWork.RunLoop("Update install when idle", WaitForIdleThenInstallAsync, _shutdown);
        }
    }

    private async Task WaitForIdleThenInstallAsync(CancellationToken cancellationToken)
    {
        await new IdleInstall(_runtimeHome, _clock).WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
        if (!StillInstallsByItself())
        {
            return;
        }
        TrayLog.Write($"Weir has been idle for {IdleInstall.IdlePeriodText}; installing update v{PendingVersion} now.");
        _callbacks.OnUi(_callbacks.ApplyNow);
    }

    // The choice is read again here: the operator may have switched to Download only or Notify only while the update
    // waited, and this is the last point before Weir restarts.
    private bool StillInstallsByItself()
    {
        var mode = UpdateSettings.Load(_runtimeHome).Mode;
        if (mode != UpdateMode.Auto)
        {
            TrayLog.Write($"Update v{PendingVersion} is waiting, but the update mode is now {mode}, so Weir does not install it by itself.");
            return false;
        }
        return _service.IsDownloaded;
    }

    // asked: a person pressed the button, so a step that cannot be taken is answered by writing the state as it is.
    internal async Task CheckAsync(bool asked = false)
    {
        // A downloaded update waits to be installed; looking again would make the tray forget it.
        if (_service.IsDownloaded || !TryBegin(UpdateActivity.Checking))
        {
            if (asked)
            {
                RepublishState();
            }
            return;
        }
        try
        {
            var check = await _service.CheckForUpdateAsync().ConfigureAwait(false);
            if (!check.Done)
            {
                WriteState(check.Failure is null ? UpdatePhase.Idle : UpdatePhase.Failed, PendingVersion, check.Failure);
                return;
            }

            var mode = _settings.Mode;
            if (mode == UpdateMode.NotifyOnly)
            {
                WriteState(UpdatePhase.Idle, PendingVersion);
                _callbacks.OnUi(() => _callbacks.Announce(UpdateMode.NotifyOnly));
                return;
            }

            SetActivity(UpdateActivity.Downloading);
            await DownloadFoundAsync(mode).ConfigureAwait(false);
        }
        finally
        {
            SetActivity(UpdateActivity.Idle);
        }
    }

    // A person's click is the choice to download, so this runs in every mode, and finds the update first when the tray
    // has not looked yet.
    internal async Task DownloadAsync(bool asked = false)
    {
        if (_service.IsDownloaded || !TryBegin(UpdateActivity.Downloading))
        {
            if (asked)
            {
                RepublishState();
            }
            return;
        }
        try
        {
            if (!_service.HasPendingUpdate)
            {
                var check = await _service.CheckForUpdateAsync().ConfigureAwait(false);
                if (!check.Done)
                {
                    WriteState(UpdatePhase.Failed, PendingVersion, check.Failure ?? NoNewerVersion);
                    return;
                }
            }

            await DownloadFoundAsync(UpdateMode.DownloadOnly).ConfigureAwait(false);
        }
        finally
        {
            SetActivity(UpdateActivity.Idle);
        }
    }

    private async Task DownloadFoundAsync(UpdateMode announce)
    {
        var download = await _service.DownloadUpdateAsync().ConfigureAwait(false);
        if (!download.Done)
        {
            WriteState(UpdatePhase.Failed, PendingVersion, download.Failure ?? NoNewerVersion);
            return;
        }

        WriteState(UpdatePhase.Downloaded, PendingVersion);
        if (_settings.Mode == UpdateMode.Auto)
        {
            InstallWhenIdle();
        }
        _callbacks.OnUi(() => _callbacks.Announce(announce));
    }

    // One check or download at a time: the periodic check, a menu click and a button in System can otherwise overlap.
    private bool TryBegin(UpdateActivity activity)
    {
        if (Interlocked.CompareExchange(ref _activity, (int)activity, (int)UpdateActivity.Idle) != (int)UpdateActivity.Idle)
        {
            return false;
        }
        ShowActivity(activity);
        return true;
    }

    private void SetActivity(UpdateActivity activity)
    {
        Volatile.Write(ref _activity, (int)activity);
        ShowActivity(activity);
    }

    // Going idle writes nothing: whatever ended the activity has already written how it ended.
    private void ShowActivity(UpdateActivity activity)
    {
        if (activity != UpdateActivity.Idle)
        {
            WriteState(activity == UpdateActivity.Checking ? UpdatePhase.Checking : UpdatePhase.Downloading, PendingVersion);
        }
        _callbacks.OnUi(_callbacks.Changed);
    }

    // Read by the server, which shows it in System › About.
    private void WriteState(UpdatePhase phase, string? version, string? failure = null)
    {
        lock (_stateLock)
        {
            _lastState = (phase, version, failure);
            WriteLastState();
        }
    }

    // A request that was not taken up changes nothing, but its flag is gone and the page was told it was under way: writing
    // the state as it last was lets the page hear what is true now.
    private void RepublishState()
    {
        lock (_stateLock)
        {
            WriteLastState();
        }
    }

    private void WriteLastState()
    {
        try
        {
            UpdateStateFile.Write(_runtimeHome, _lastState.Phase, _lastState.Version, _lastState.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not write update state: {ex.Message}");
        }
    }
}
