using System.Security.AccessControl;
using System.Security.Principal;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The secrets sign sessions and encrypt stored credentials, and the runtime home under %ProgramData% is readable
/// by every local user by default. Each secret file must allow only SYSTEM, Administrators and the tray's account,
/// and a usable secret must never be replaced.
/// </summary>
public sealed class SecretFileTests : IDisposable
{
    private const string FileName = "test.secret";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    private string SecretPath => Path.Combine(_home.Path, FileName);

    [Fact]
    public void A_new_secret_is_long_enough_and_saved()
    {
        var secret = SecretFile.Ensure(_home.Path, FileName);

        Assert.True(secret.Length >= SecretFile.MinimumLength);
        Assert.Equal(secret, File.ReadAllText(SecretPath));
        Assert.Empty(Directory.GetFiles(_home.Path, "*.tmp"));
    }

    [Fact]
    public void A_new_secret_file_is_owner_only()
    {
        SecretFile.Ensure(_home.Path, FileName);

        Assert.True(OwnerOnlyAccess.IsOwnerOnly(new FileInfo(SecretPath).GetAccessControl()));
    }

    [Fact]
    public void A_saved_secret_is_kept()
    {
        var saved = new string('s', 40);
        File.WriteAllText(SecretPath, saved);

        var secret = SecretFile.Ensure(_home.Path, FileName);

        Assert.Equal(saved, secret);
        Assert.Equal(saved, File.ReadAllText(SecretPath));
    }

    [Fact]
    public void A_saved_secret_that_everyone_can_read_is_tightened()
    {
        File.WriteAllText(SecretPath, new string('s', 40));
        GrantEveryoneRead(SecretPath);

        SecretFile.Ensure(_home.Path, FileName);

        Assert.True(OwnerOnlyAccess.IsOwnerOnly(new FileInfo(SecretPath).GetAccessControl()));
    }

    [Fact]
    public void Restricting_leaves_exactly_system_administrators_and_this_account()
    {
        File.WriteAllText(SecretPath, new string('s', 40));

        SecretFile.Restrict(SecretPath);

        var security = new FileInfo(SecretPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(OwnerOnlyAccess.AllowedAccounts(), Accounts(security));
    }

    [Fact]
    public void An_owner_only_file_is_left_as_it_is()
    {
        File.WriteAllText(SecretPath, new string('s', 40));
        SecretFile.Restrict(SecretPath);

        Assert.False(SecretFile.Restrict(SecretPath));
    }

    [Fact]
    public void A_saved_secret_another_program_has_open_for_a_moment_is_read_when_it_lets_go_and_kept()
    {
        var saved = new string('k', SecretFile.MinimumLength);
        File.WriteAllText(SecretPath, saved);
        var held = new FileStream(SecretPath, FileMode.Open, FileAccess.Read, FileShare.None);

        // A virus scanner holds a file it is looking at; it lets go before the reader's next look.
        var secret = SecretFile.Ensure(_home.Path, FileName, _ => held.Dispose());

        Assert.Equal(saved, secret);
        Assert.Equal(saved, File.ReadAllText(SecretPath));
    }

    [Fact]
    public void A_saved_secret_that_stays_unreadable_is_an_error_and_is_not_replaced()
    {
        var saved = new string('k', SecretFile.MinimumLength);
        File.WriteAllText(SecretPath, saved);
        using var held = new FileStream(SecretPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<InvalidOperationException>(() => SecretFile.Ensure(_home.Path, FileName, _ => { }));

        held.Dispose();
        Assert.Equal(saved, File.ReadAllText(SecretPath));
    }

    [Fact]
    public void A_secret_too_short_to_use_is_replaced()
    {
        File.WriteAllText(SecretPath, "short");

        var secret = SecretFile.Ensure(_home.Path, FileName);

        Assert.True(secret.Length >= SecretFile.MinimumLength);
        Assert.Equal(secret, File.ReadAllText(SecretPath));
    }

    [Fact]
    public void Two_secrets_in_one_home_are_different()
    {
        var session = SecretFile.Ensure(_home.Path, ServerEnvironment.SessionSecretFileName);
        var credentials = SecretFile.Ensure(_home.Path, ServerEnvironment.CredentialsSecretFileName);

        Assert.NotEqual(session, credentials);
    }


    [Fact]
    public void A_saved_secret_whose_access_list_cannot_be_tightened_is_still_used_and_the_log_says_so()
    {
        var saved = new string('s', SecretFile.MinimumLength);
        File.WriteAllText(SecretPath, saved);

        var secret = SecretFile.Ensure(_home.Path, FileName, restrict: _ => throw new UnauthorizedAccessException("denied"));

        Assert.Equal(saved, secret);
        Assert.Contains($"Could not restrict who can read {SecretPath} (denied); it keeps its current access list.", LogFile.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_secret_that_was_tightened_is_logged_as_restricted()
    {
        File.WriteAllText(SecretPath, new string('s', SecretFile.MinimumLength));

        SecretFile.Ensure(_home.Path, FileName, restrict: _ => true);

        Assert.Contains($"Restricted {SecretPath} to SYSTEM, Administrators and", LogFile.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_file_that_is_gone_when_it_is_read_has_no_secret()
    {
        Assert.Equal(string.Empty, SecretFile.ReadSaved(SecretPath, pause: null));
    }

    internal static HashSet<SecurityIdentifier> Accounts(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();

    internal static void GrantEveryoneRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
    }
}
