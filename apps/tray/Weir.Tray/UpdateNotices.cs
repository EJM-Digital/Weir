namespace Weir.Tray;

/// <summary>A balloon about an update: its words, what tray-host.log says of it, and what a click on it does (nothing when null).</summary>
readonly record struct UpdateNotice(string Title, string Text, string Log, Action? OnClick);

/// <summary>What the tray tells the person about an update, by the mode they chose.</summary>
static class UpdateNotices
{
    internal const string ReadyTitle = "Weir Update Ready";
    internal const string AvailableTitle = "Weir Update";

    /// <summary>
    /// Automatic: the update installs by itself once Weir has been idle, so there is nothing to click. Download only: a click restarts
    /// Weir to install it. Notify only: a click opens System › About, where the update is downloaded.
    /// </summary>
    internal static UpdateNotice For(UpdateMode mode, string version, Action restartToUpdate, Action openAbout) => mode switch
    {
        UpdateMode.Auto => new UpdateNotice(
            ReadyTitle,
            $"Version {version} has been downloaded. Weir installs it and restarts by itself once it has been idle for {IdleInstall.IdlePeriodText}, or when Weir next quits or starts.",
            $"Notifying user: update v{version} downloaded; it installs once Weir has been idle for {IdleInstall.IdlePeriodText}, or when Weir next quits or starts.",
            null),
        UpdateMode.DownloadOnly => new UpdateNotice(
            ReadyTitle,
            $"Version {version} has been downloaded. Click here to restart and install it, or it installs the next time Weir quits or starts.",
            $"Notifying user: update v{version} downloaded and ready to install.",
            restartToUpdate),
        _ => new UpdateNotice(
            AvailableTitle,
            $"Version {version} is available. Open System › About in Weir to update.",
            $"Notifying user: update v{version} available.",
            openAbout),
    };
}

/// <summary>
/// Restart to install the downloaded update, from the menu, the balloon or System › About. Nothing happens when no update is
/// downloaded; when an install is already under way the person is told so, instead of a second one starting.
/// </summary>
/// <param name="isDownloaded">Whether an update is downloaded and waiting.</param>
/// <param name="restartToUpdate">Starts the install; false when one is already under way.</param>
/// <param name="balloon">Shows a balloon: title, text, icon and what a click on it does.</param>
/// <param name="log">Records what the tray did.</param>
sealed class UpdateRestart(Func<bool> isDownloaded, Func<Task<bool>> restartToUpdate, Action<string, string, ToolTipIcon, Action?> balloon, Action<string> log)
{
    internal async Task RunAsync()
    {
        if (!isDownloaded())
        {
            return;
        }
        log("Applying the downloaded update and restarting.");
        if (!await restartToUpdate())
        {
            balloon(UpdateNotices.AvailableTitle, TrayBalloons.UpdateUnderWayText, ToolTipIcon.Info, null);
        }
    }
}
