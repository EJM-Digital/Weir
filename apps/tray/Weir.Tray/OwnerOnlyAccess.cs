using System.Security.AccessControl;
using System.Security.Principal;

namespace Weir.Tray;

/// <summary>
/// The access list the runtime home and its secrets get: full control for SYSTEM, Administrators and the account the
/// tray runs as, and nothing inherited from the folder above. The runtime home defaults to %ProgramData%\Weir, which
/// every local user can read (and create files in) by default; it holds the database, backups, logs and the secrets
/// that sign sessions and encrypt stored API keys.
/// </summary>
static class OwnerOnlyAccess
{
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>NT SERVICE\TrustedInstaller, which owns folders Windows itself creates.</summary>
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>SYSTEM, Administrators and the tray's own account.</summary>
    internal static HashSet<SecurityIdentifier> AllowedAccounts() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        CurrentUser(),
    ];

    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Required(identity.User);
    }

    internal static SecurityIdentifier Required(SecurityIdentifier? user) =>
        user ?? throw new InvalidOperationException("The tray's Windows account has no security identifier.");

    /// <summary>
    /// Whether a folder owned by <paramref name="owner"/> can be taken over as this account's runtime home: owned by
    /// this account, or by Windows or its administrators. A folder another user account owns may hold that
    /// person's data, or have been planted to read this account's.
    /// </summary>
    internal static bool IsTrustedOwner(SecurityIdentifier owner, SecurityIdentifier currentUser) =>
        owner == currentUser
        || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || owner == TrustedInstaller;

    internal static FileSecurity ForFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in AllowedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>Owner-only for the folder, inherited by everything in it, including files created later.</summary>
    internal static DirectorySecurity ForDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in AllowedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>Whether <paramref name="security"/> inherits nothing and allows only the allowed accounts.</summary>
    internal static bool IsOwnerOnly(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }
        var allowed = AllowedAccounts();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <see cref="IsOwnerOnly"/>, and each allowed account has full control that the folder's contents inherit, so
    /// files created later are covered too.
    /// </summary>
    internal static bool IsOwnerOnlyForContents(DirectorySecurity security)
    {
        if (!IsOwnerOnly(security))
        {
            return false;
        }
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        return AllowedAccounts().All(account => rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && (SecurityIdentifier)rule.IdentityReference == account
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)
            && rule.InheritanceFlags == ToChildren
            && rule.PropagationFlags == PropagationFlags.None));
    }
}
