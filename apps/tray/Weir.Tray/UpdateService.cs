using Velopack;

namespace Weir.Tray;

/// <summary>Checks for, downloads and applies Weir updates from the GitHub releases, through Velopack.</summary>
sealed class UpdateService : IUpdateService
{
    private readonly UpdateManager _mgr;
    private readonly Action<string> _log;
    private volatile UpdateInfo? _pendingUpdate;
    private volatile bool _downloaded;
    private volatile int _downloadProgress;

    public bool HasPendingUpdate => _pendingUpdate is not null;
    public bool IsDownloaded => _downloaded;
    internal int DownloadProgress => _downloadProgress;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    internal UpdateService(Action<string> log)
    {
        _log = log;
        _mgr = new UpdateManager(UpdateChannel.SourceForThisBuild(ProjectLocation.RepositoryUrl));
    }

    public bool IsInstalled => _mgr.IsInstalled;

    // Velopack reports every failure (network, GitHub, disk) as an exception; each one is logged and handed back in
    // plain words, so these return an outcome rather than throw.
    public async Task<UpdateOutcome> CheckForUpdateAsync()
    {
        try
        {
            _log("Checking for updates...");
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _log("No update available.");
                _pendingUpdate = null;
                _downloaded = false;
                return UpdateOutcome.NothingToDo;
            }
            _pendingUpdate = info;
            _downloaded = false;
            _downloadProgress = 0;
            _log($"Update available: v{info.TargetFullRelease.Version}");
            return UpdateOutcome.Succeeded;
        }
        catch (Exception ex)
        {
            _log($"Update check failed: {ex.Message}");
            return UpdateOutcome.Failed(UpdateFailureText.ForCheck(ex));
        }
    }

    public async Task<UpdateOutcome> DownloadUpdateAsync()
    {
        var pending = _pendingUpdate;
        if (pending is null)
        {
            return UpdateOutcome.NothingToDo;
        }
        try
        {
            _log($"Downloading update v{pending.TargetFullRelease.Version}...");
            await _mgr.DownloadUpdatesAsync(pending, p => _downloadProgress = p).ConfigureAwait(false);
            _downloaded = true;
            _log("Update downloaded successfully.");
            return UpdateOutcome.Succeeded;
        }
        catch (Exception ex)
        {
            _log($"Update download failed: {ex.Message}");
            return UpdateOutcome.Failed(UpdateFailureText.ForDownload(ex));
        }
    }

    public string? FindUpdateLeftWaiting()
    {
        if (!IsInstalled)
        {
            return null;
        }
        try
        {
            return _mgr.UpdatePendingRestart?.Version?.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not look for an update left waiting: {ex.Message}");
            return null;
        }
    }

    public void ApplyAndExit()
    {
        var pending = _pendingUpdate;
        if (!_downloaded || pending is null)
        {
            return;
        }
        _log($"Applying update v{pending.TargetFullRelease.Version} and exiting...");
        HandOver(pending.TargetFullRelease, UpdateHandOver.ThenStayStopped);
        Environment.Exit(0);
    }

    public void ApplyAndRestart()
    {
        var pending = _pendingUpdate;
        if (!_downloaded || pending is null)
        {
            return;
        }
        _log($"Applying update v{pending.TargetFullRelease.Version} and restarting...");
        HandOver(pending.TargetFullRelease, UpdateHandOver.ThenRestart);
        Environment.Exit(0);
    }

    public void ApplyLeftWaitingUpdateAndRestart()
    {
        var waiting = _mgr.UpdatePendingRestart;
        if (waiting is null)
        {
            return;
        }
        _log($"Applying update v{waiting.Version} left waiting, and restarting...");
        HandOver(waiting, UpdateHandOver.ThenRestart);
    }

    // Velopack's own ApplyUpdatesAndRestart is not silent; this is the call it makes, told to be (#869).
    private void HandOver(VelopackAsset update, UpdateHandOver handOver) =>
        _mgr.WaitExitThenApplyUpdates(update, handOver.Silent, handOver.Restart, handOver.RestartArguments);
}
