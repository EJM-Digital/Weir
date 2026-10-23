using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>Whether a Windows access list is the private one Weir gives its copies: nothing inherited, and only its own accounts allowed.</summary>
[SupportedOSPlatform("windows")]
public sealed class OwnerOnlyAccessTests
{
    private const string OnlyWindows = "Access lists are Windows'.";

    private static FileSecurity Protected()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in OwnerOnlyAccess.AllowedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static FileSystemAccessRule ForEveryone(AccessControlType type) =>
        new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, type);

    [WindowsFact(OnlyWindows)]
    public void A_list_that_inherits_from_the_folder_above_is_not_private()
    {
        Assert.False(OwnerOnlyAccess.IsOwnerOnly(new FileSecurity()));
    }

    [WindowsFact(OnlyWindows)]
    public void A_protected_list_that_allows_only_Weirs_accounts_is_private()
    {
        Assert.True(OwnerOnlyAccess.IsOwnerOnly(Protected()));
    }

    [WindowsFact(OnlyWindows)]
    public void A_protected_list_that_allows_anyone_else_is_not_private()
    {
        var security = Protected();
        security.AddAccessRule(ForEveryone(AccessControlType.Allow));

        Assert.False(OwnerOnlyAccess.IsOwnerOnly(security));
    }

    [WindowsFact(OnlyWindows)]
    public void A_protected_list_that_only_refuses_someone_else_is_still_private()
    {
        var security = Protected();
        security.AddAccessRule(ForEveryone(AccessControlType.Deny));

        Assert.True(OwnerOnlyAccess.IsOwnerOnly(security));
    }
}
