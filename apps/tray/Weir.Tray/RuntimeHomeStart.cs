using System.Security.AccessControl;

namespace Weir.Tray;

/// <summary>
/// The tray's first look at the runtime home when a Windows account starts Weir. The folder is one place for the whole PC, so a
/// second account (fast user switching, Remote Desktop) can find it made by the first. Weir does not use a folder another account
/// owns, since it may hold that person's data or have been made by someone else to read yours; the person is told why, and how to
/// move on. Each account's tray keeps its own single-instance lock and second-launch signal (<see cref="Names"/>), so the two
/// trays do not take each other for a second launch.
/// </summary>
static class RuntimeHomeStart
{
    /// <summary>The names of what keeps one tray to each Windows session. <c>Local\</c> is the session's own namespace.</summary>
    internal static class Names
    {
        internal const string SessionPrefix = @"Local\";
        internal const string SingleInstanceLock = SessionPrefix + "WeirTrayHostSingleton";
    }

    /// <summary>
    /// Creates the runtime home owner-only, or tightens it. Returns false when Weir must not start because another account
    /// owns the folder; the person at the desktop, if any, is told why.
    /// </summary>
    /// <param name="runtimeHome">The folder.</param>
    /// <param name="secure">Makes it owner-only; true when it changed the access list (<see cref="RuntimeHomeSecurity.Secure(string)"/>).</param>
    /// <param name="hasDesktop">Whether there is a person to show a message to.</param>
    /// <param name="showMessage">Shows a message to that person.</param>
    /// <param name="log">Records what the tray did.</param>
    internal static bool Secure(string runtimeHome, Func<string, bool> secure, Func<bool> hasDesktop, Action<string> showMessage, Action<string> log)
    {
        try
        {
            if (secure(runtimeHome))
            {
                log($"Restricted {runtimeHome} to SYSTEM, Administrators and {Environment.UserName}.");
            }
            return true;
        }
        catch (RuntimeHomeOwnedByAnotherAccountException ex)
        {
            log($"Not starting: {ex.Message}");
            if (hasDesktop())
            {
                showMessage(ex.Message);
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            // Weir still starts: its data works, it is only readable by more accounts than it should be.
            log($"Could not restrict who can read {runtimeHome} ({ex.Message}); it keeps its current access list.");
            return true;
        }
    }
}
