using System.Security.AccessControl;
using System.Security.Principal;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>What counts as owner-only: only the allowed accounts hold a way in, and a rule that denies someone is not a way in.</summary>
public sealed class OwnerOnlyAccessTests
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    [Fact]
    public void A_security_that_allows_another_account_in_is_not_owner_only()
    {
        var security = OwnerOnlyAccess.ForDirectory();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));

        Assert.False(OwnerOnlyAccess.IsOwnerOnly(security));
    }

    [Fact]
    public void A_rule_that_denies_another_account_does_not_open_the_folder_to_it()
    {
        var security = OwnerOnlyAccess.ForDirectory();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Deny));

        Assert.True(OwnerOnlyAccess.IsOwnerOnly(security));
    }

    [Fact]
    public void A_security_that_inherits_from_its_parent_is_not_owner_only()
    {
        var security = OwnerOnlyAccess.ForFile();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

        Assert.False(OwnerOnlyAccess.IsOwnerOnly(security));
    }

    [Fact]
    public void An_account_without_a_security_identifier_is_refused_with_a_plain_message()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => OwnerOnlyAccess.Required(null));

        Assert.Equal("The tray's Windows account has no security identifier.", failure.Message);
    }

    [Fact]
    public void An_account_with_a_security_identifier_is_returned_as_it_is()
    {
        Assert.Equal(Everyone, OwnerOnlyAccess.Required(Everyone));
    }
}

/// <summary>The parent process Windows reports.</summary>
public sealed class ParentProcessTests
{
    [Fact]
    public void A_zero_status_gives_the_parent_Windows_reported()
    {
        Assert.Equal(4242, ParentProcess.Parent(status: 0, inheritedFrom: 4242));
    }

    [Theory]
    [InlineData(-1073741790)]
    [InlineData(1)]
    public void Any_other_status_gives_no_parent(int status)
    {
        Assert.Null(ParentProcess.Parent(status, inheritedFrom: 4242));
    }
}
