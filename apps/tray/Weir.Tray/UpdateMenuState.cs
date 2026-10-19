namespace Weir.Tray;

/// <summary>
/// The tray's update menu item: its text, whether it is enabled, and what a click does. The item has one click
/// handler that asks for the current state, so no handler is ever added twice however often the state changes.
/// </summary>
sealed record UpdateMenuState(string Text, bool Enabled, UpdateMenuAction Action)
{
    internal static UpdateMenuState Describe(
        bool isInstalled,
        UpdateActivity activity,
        bool hasPendingUpdate,
        bool isDownloaded,
        string? pendingVersion)
    {
        if (!isInstalled)
        {
            return new("Check for updates", true, UpdateMenuAction.OpenUpdatePage);
        }
        return activity switch
        {
            UpdateActivity.Checking => new("Checking for updates...", false, UpdateMenuAction.Wait),
            UpdateActivity.Downloading => new("Downloading update...", false, UpdateMenuAction.Wait),
            _ when isDownloaded => new($"Restart to update (v{pendingVersion})", true, UpdateMenuAction.Restart),
            _ when hasPendingUpdate => new($"Download update (v{pendingVersion})", true, UpdateMenuAction.Download),
            _ => new("Check for updates", true, UpdateMenuAction.Check),
        };
    }
}
