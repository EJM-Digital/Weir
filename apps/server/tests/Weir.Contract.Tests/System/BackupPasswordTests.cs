using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.MediaManagers;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// The backup password. With one set, a backup holds the connections' secrets only locked with it, and a restore on another install
/// asks for it; with none, a backup holds no secret, as it always has. A wrong password refuses plainly and changes nothing, and a
/// backup made before a password was set still restores.
/// </summary>
[ContractArea("system")]
public sealed class BackupPasswordTests
{
    private const string Password = "contract backup password";
    private const string WrongPassword = "not the backup password";
    private const string NewPassword = "a different backup password";

    private static string[] SecretsOf(BackupWorld world) =>
    [
        BackupWorld.Secret, world.Manager.ApiKey, "hooks.example.com/one", "discord.example.com/api/webhooks/two",
    ];

    private static async Task<int> CountAsync(BackupWorld world, string route) => (await world.Admin.GetAsync(route)).Elements.Count;

    [Fact]
    public async Task A_backup_with_no_password_holds_no_secret_and_says_so_and_a_password_changes_what_the_next_one_holds()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        Assert.False((bool)(await world.Admin.GetAsync(BackupWorld.Backups)).Fields["password_set"]!);

        var (before, beforeFile) = await world.BackUpNowAsync();
        await world.SetBackupPasswordAsync(Password);
        var (after, afterFile) = await world.BackUpNowAsync();

        Assert.False((bool)before["secrets_sealed"]!);
        Assert.DoesNotContain("sealed_secrets", beforeFile, StringComparison.Ordinal);
        Assert.True((bool)after["secrets_sealed"]!);
        Assert.Contains("\"sealed_secrets\"", afterFile, StringComparison.Ordinal);
        Assert.True((bool)(await world.Admin.GetAsync(BackupWorld.Backups)).Fields["password_set"]!);
        foreach (var secret in SecretsOf(world))
        {
            Assert.DoesNotContain(secret, beforeFile, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, afterFile, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Password, afterFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_backup_restores_on_another_install_with_its_password_and_brings_the_secrets_with_it()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        await world.SetBackupPasswordAsync(Password);
        var (_, file) = await world.BackUpNowAsync();
        await using var other = await BackupWorld.StartAsync();

        var restored = await other.RestoreAsync(JsonNode.Parse(file)!, Password);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        var manager = Assert.Single((await other.Admin.GetAsync(ManagerConnections.Route)).Elements)!;
        Assert.True((bool)manager["api_key_is_saved"]!, "the media manager's API key came back");
        Assert.False((bool)manager["enabled"]!, "a restored connection is switched off");
        var client = Assert.Single((await other.Admin.GetAsync(DownloadClients.Route)).Elements)!;
        Assert.True((bool)client["api_key_is_saved"]!, "the download client's API key came back");
        Assert.False((bool)client["enabled"]!);
        var alerts = await other.AlertsAsync();
        Assert.Equal("https://hooks.example.com/••••", (string)alerts["Webhook alert"]["url"]!);
        Assert.Equal("https://discord.example.com/api/webhooks/••••", (string)alerts["Discord alert"]["url"]!);
    }

    [Fact]
    public async Task A_wrong_or_missing_password_refuses_plainly_and_changes_nothing()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        await world.SetBackupPasswordAsync(Password);
        var (_, file) = await world.BackUpNowAsync();
        await using var other = await BackupWorld.StartAsync();

        var wrong = await other.RestoreAsync(JsonNode.Parse(file)!, WrongPassword);
        var missing = await other.RestoreAsync(JsonNode.Parse(file)!);

        Assert.Equal(
            (HttpStatusCode.BadRequest, "That is not the backup password this backup was made with. Nothing was changed."), (wrong.Status, (string)wrong.Fields["detail"]!));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "This backup holds secrets locked with a backup password. Enter the backup password to restore it."),
            (missing.Status, (string)missing.Fields["detail"]!));
        Assert.Equal(0, await CountAsync(other, ManagerConnections.Route));
        Assert.Equal(0, await CountAsync(other, DownloadClients.Route));
        Assert.Empty(await other.AlertsAsync());
    }

    [Fact]
    public async Task A_backup_made_before_the_password_was_set_still_restores_with_no_password_and_brings_no_secret()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var (_, older) = await world.BackUpNowAsync();
        await world.SetBackupPasswordAsync(Password);
        await using var other = await BackupWorld.StartAsync();

        var restored = await other.RestoreAsync(JsonNode.Parse(older)!);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        var manager = Assert.Single((await other.Admin.GetAsync(ManagerConnections.Route)).Elements)!;
        Assert.False((bool)manager["api_key_is_saved"]!);
        Assert.Equal(0, await CountAsync(other, DownloadClients.Route));
    }

    [Fact]
    public async Task Changing_the_password_locks_later_backups_with_the_new_one_and_leaves_older_ones_on_the_old_one()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        await world.SetBackupPasswordAsync(Password);
        var (_, older) = await world.BackUpNowAsync();
        await world.SetBackupPasswordAsync(NewPassword);
        var (_, newer) = await world.BackUpNowAsync();
        await using var other = await BackupWorld.StartAsync();

        var olderWithNew = await other.RestoreAsync(JsonNode.Parse(older)!, NewPassword);
        var newerWithOld = await other.RestoreAsync(JsonNode.Parse(newer)!, Password);
        var olderWithOld = await other.RestoreAsync(JsonNode.Parse(older)!, Password);
        var newerWithNew = await other.RestoreAsync(JsonNode.Parse(newer)!, NewPassword);

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.OK],
            [olderWithNew.Status, newerWithOld.Status, olderWithOld.Status, newerWithNew.Status]);
    }

    [Theory]
    [InlineData("short", "short", "Password must be at least 8 characters.")]
    [InlineData("a good password", "another good password", "The two backup passwords do not match. Type the same password in both boxes.")]
    public async Task A_password_that_is_too_weak_or_typed_differently_is_refused_in_plain_words_and_none_is_set(string password, string confirmation, string reason)
    {
        await using var world = await BackupWorld.StartAsync();

        var refused = await world.Admin.PutWithCsrfAsync(BackupWorld.BackupPassword, new JsonObject { ["password"] = password, ["confirm_password"] = confirmation });

        Assert.Equal((HttpStatusCode.BadRequest, reason), (refused.Status, (string)refused.Fields["detail"]!));
        Assert.False((bool)(await world.Admin.GetAsync(BackupWorld.Backups)).Fields["password_set"]!);
    }
}

/// <summary>Who may set the backup password.</summary>
[ContractArea("system")]
public sealed class BackupPasswordAccessTests(UsersFixture fixture) : IClassFixture<UsersFixture>
{
    [Fact]
    public async Task Only_an_administrator_can_set_the_backup_password()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);

        var refused = await viewer.PutWithCsrfAsync(BackupWorld.BackupPassword, new JsonObject { ["password"] = "contract backup password", ["confirm_password"] = "contract backup password" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
    }
}
