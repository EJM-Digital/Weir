using System.Security.Principal;

namespace Weir.Tray;

/// <summary>
/// Makes the runtime home owner-only (<see cref="OwnerOnlyAccess"/>) on every start: the database, backups, logs,
/// tools, temp files, the update trigger file and the secrets all live in it, and all inherit its access list.
/// </summary>
static class RuntimeHomeSecurity
{
    /// <summary>
    /// Creates <paramref name="runtimeHome"/> owner-only, or tightens it if it exists and is looser. Returns whether
    /// it changed anything. Throws <see cref="RuntimeHomeOwnedByAnotherAccountException"/> when another user account
    /// owns it, and never changes that folder.
    /// </summary>
    internal static bool Secure(string runtimeHome) =>
        Secure(runtimeHome, owner => OwnerOnlyAccess.IsTrustedOwner(owner, OwnerOnlyAccess.CurrentUser()));

    /// <param name="runtimeHome">The folder.</param>
    /// <param name="isTrustedOwner">Whether a folder owned by this account may be used.</param>
    internal static bool Secure(string runtimeHome, Func<SecurityIdentifier, bool> isTrustedOwner)
    {
        var directory = new DirectoryInfo(runtimeHome);
        if (!directory.Exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runtimeHome))!);
            directory.Create(OwnerOnlyAccess.ForDirectory());
            return true;
        }

        var security = directory.GetAccessControl();
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        if (!isTrustedOwner(owner))
        {
            throw new RuntimeHomeOwnedByAnotherAccountException(runtimeHome, Describe(owner));
        }

        if (OwnerOnlyAccess.IsOwnerOnlyForContents(security))
        {
            return false;
        }
        // Setting a protected list on the folder re-applies inheritance to everything already in it, so files that
        // inherited "Users can read" from %ProgramData% lose it too. A file with its own protected list (the
        // secrets) keeps it.
        directory.SetAccessControl(OwnerOnlyAccess.ForDirectory());
        return true;
    }

    internal static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
