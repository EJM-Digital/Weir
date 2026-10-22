namespace Weir.Tray;

/// <summary>How this run was started.</summary>
/// <param name="AnnounceOnReady">Whether a balloon tells the person Weir is running once its server is ready.</param>
/// <param name="Interactive">Whether there is a person to tell when the server cannot start. A start with no one to tell exits instead.</param>
readonly record struct TrayStart(bool AnnounceOnReady, bool Interactive)
{
    /// <summary>Whether the person started Weir themselves: not silent, not at sign-in or by another program (both pass --no-browser), and with a desktop.</summary>
    internal bool StartedByPerson => AnnounceOnReady && Interactive;
}
