namespace Weir.Tray;

/// <summary>
/// Installing an update that an earlier run downloaded and never got to install: Windows ended the session, or the
/// tray was killed, before the person quit Weir. It happens before the server starts, when nothing is running that
/// an install could interrupt (#857).
/// </summary>
static class UpdateOnStart
{
    internal const string AttemptFileName = "update-start-attempt";

    /// <summary>
    /// Installs the update left waiting, if there is one and the person's update choice allows an install. Returns
    /// true when the installer has been started and the tray must exit at once. Each update is tried once from
    /// here: an install that fails puts the old tray back, which would otherwise try again forever.
    /// </summary>
    /// <remarks>
    /// A server left running by a tray that was killed is stopped first. If it still runs and can save a copy of Weir's data
    /// (<paramref name="orphanRuns"/>), it is asked for one before it is stopped, under the same rules as any other install: when
    /// the copy cannot be saved the update is left waiting and Weir starts as it is. With no such server there is nothing to ask,
    /// and an update whose copy has already failed is left alone (the server that started after it would fail the same way).
    /// </remarks>
    /// <param name="updates">Velopack.</param>
    /// <param name="mode">The person's update choice.</param>
    /// <param name="runtimeHome">The data folder.</param>
    /// <param name="stopOrphanedServers">Stops a server left running by a tray that was killed.</param>
    /// <param name="backup">Asks a running server for the copy; null asks nothing.</param>
    /// <param name="orphanRuns">Whether a server of this install is still running.</param>
    internal static bool TryApply(
        IUpdateService updates,
        UpdateMode mode,
        string runtimeHome,
        Action stopOrphanedServers,
        IUpdateBackup? backup = null,
        Func<bool>? orphanRuns = null)
    {
        if (mode == UpdateMode.NotifyOnly || updates.FindUpdateLeftWaiting() is not { } version)
        {
            return false;
        }
        if (backup is not null && orphanRuns?.Invoke() == true)
        {
            TrayLog.Write($"Update v{version} was left waiting, and a server is still running; asking it to save a copy of Weir's data first.");
            if (!backup.SaveBeforeApplyAsync(version).GetAwaiter().GetResult())
            {
                TrayLog.Write($"Update v{version} is left waiting, because the copy of Weir's data could not be saved. Starting Weir as it is.");
                return false;
            }
        }
        else if (UpdateBackupHook.FailedFor(runtimeHome, version))
        {
            TrayLog.Write($"Update v{version} is waiting, but Weir could not save a copy of its data before applying it, and the server that starts after the update would fail the same way. Starting Weir as it is; the next update check tries again.");
            return false;
        }
        if (WasAttempted(runtimeHome, version))
        {
            TrayLog.Write($"Update v{version} is waiting, but installing it at start-up was tried before and did not finish. Starting Weir as it is.");
            return false;
        }
        if (!TryRecordAttempt(runtimeHome, version))
        {
            return false;
        }
        TrayLog.Write($"Update v{version} was left waiting to install. Installing it before the server starts.");
        stopOrphanedServers();
        updates.ApplyLeftWaitingUpdateAndRestart();
        return true;
    }

    // A record that cannot be read counts as an earlier attempt: without it a failing install could not be told
    // from a first one.
    private static bool WasAttempted(string runtimeHome, string version)
    {
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, AttemptFileName));
        switch (reading.State)
        {
            case ReplacedFileState.Busy:
                TrayLog.Write($"Could not read {AttemptFileName} (it stayed busy).");
                return true;
            case ReplacedFileState.Absent:
                return false;
            default:
                return reading.Text.Trim() == version;
        }
    }

    private static bool TryRecordAttempt(string runtimeHome, string version)
    {
        try
        {
            AtomicFile.WriteAllText(runtimeHome, AttemptFileName, version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Update v{version} is waiting, but Weir could not record trying it ({ex.Message}), so it is left for the next quit.");
            return false;
        }
    }
}
