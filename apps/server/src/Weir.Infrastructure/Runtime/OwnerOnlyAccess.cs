using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// The access list a folder of private copies gets on Windows: full control for SYSTEM, Administrators and the account Weir
/// runs as, nothing inherited from the folder above, and everything made inside inheriting the same. It is the list the tray
/// puts on Weir's data folder (<c>apps/tray/Weir.Tray/OwnerOnlyAccess.cs</c>), applied here by the server itself because the
/// backup folder may be somewhere else (<c>WEIR_BACKUP_DIR</c>) that never had it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class OwnerOnlyAccess
{
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    internal static HashSet<SecurityIdentifier> AllowedAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return
        [
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            // A signed-in account always has an identifier; only the anonymous identity has none, and the server never runs as that.
            identity.User!,
        ];
    }

    internal static void RestrictFolder(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in AllowedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    internal static void RestrictFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in AllowedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>Whether <paramref name="security"/> inherits nothing and allows only the accounts above.</summary>
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
}
