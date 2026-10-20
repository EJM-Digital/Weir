using System.Net;
using System.Text.Json.Nodes;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The backup password over real HTTP: who may set it, what it must be, what a backup made with and without it holds, and a restore
/// that needs it. The password and the secrets it protects never come back in a response, a backup file or the server log.
/// </summary>
public sealed class BackupPasswordApiTests
{
    private const string Password = "keep this somewhere safe";
    private const string BackupPassword = "/api/v1/suite/backup-password";
    private const string Backups = "/api/v1/suite/configuration-backups";
    private const string Bundle = "/api/v1/suite/configuration-bundle";
    private const string Channels = "/api/v1/suite/notification-channels";
    private const string AlertToken = "alert-token-3b9f61";
    private const string AlertAddress = "https://discord.com/api/webhooks/1034598712/" + AlertToken;

    private static async Task<(WeirTestServer Server, ApiTestClient Admin)> SignedInAdminAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<HttpResponseMessage> SetAsync(ApiTestClient client, string password, string? confirmation = null) =>
        await client.PutAsync(BackupPassword, new { csrf_token = await client.CsrfAsync(), password, confirm_password = confirmation ?? password });

    private static async Task<JsonNode> BackUpAsync(ApiTestClient client)
    {
        using var created = await client.PostAsync(Backups, new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await Json(created);
    }

    private static async Task<string> DownloadAsync(ApiTestClient client, JsonNode backup)
    {
        using var download = await client.GetAsync($"{Backups}/{backup["id"]!.GetValue<long>()}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        return await download.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> RestoreAsync(ApiTestClient client, string backupText, string? password) =>
        await client.PutAsync(
            Bundle,
            new JsonObject
            {
                ["csrf_token"] = await client.CsrfAsync(),
                ["bundle"] = JsonNode.Parse(backupText),
                ["password"] = password,
            });

    private static async Task AddAlertAsync(ApiTestClient client)
    {
        using var created = await client.PostAsync(
            Channels, new { csrf_token = await client.CsrfAsync(), label = "Failures", provider = "discord", url = AlertAddress, events = new[] { "job_failed" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Setting_the_password_is_listed_as_set_and_the_password_is_not_echoed()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        Assert.False((await Json(await admin.GetAsync(Backups)))["password_set"]!.GetValue<bool>());

        using var set = await SetAsync(admin, Password);

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var text = await set.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.True(JsonNode.Parse(text)!["password_set"]!.GetValue<bool>());
        var listed = await Json(await admin.GetAsync(Backups));
        Assert.True(listed["password_set"]!.GetValue<bool>());
        Assert.DoesNotContain(Password, listed.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_different_passwords_are_refused_in_plain_words_and_none_is_set()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var refused = await SetAsync(admin, Password, "keep this somewhere else");

        Assert.Equal((HttpStatusCode.BadRequest, "The two backup passwords do not match. Type the same password in both boxes."), (refused.StatusCode, await Detail(refused)));
        Assert.False((await Json(await admin.GetAsync(Backups)))["password_set"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("short", "Password must be at least 8 characters.")]
    [InlineData("password1", "Password is too common. Choose a stronger password.")]
    [InlineData("aaaaaaaaaa", "Password must use a wider mix of characters.")]
    public async Task A_weak_password_is_refused_with_the_reason(string password, string reason)
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var refused = await SetAsync(admin, password);

        Assert.Equal((HttpStatusCode.BadRequest, reason), (refused.StatusCode, await Detail(refused)));
        Assert.False((await Json(await admin.GetAsync(Backups)))["password_set"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Setting_the_password_is_refused_to_an_operator_and_an_anonymous_caller_and_without_the_confirmation_token()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        await TestDatabase.SeedUserAsync(server, "opal", "a-long-enough-password", "operator");
        var operatorClient = new ApiTestClient(server);
        await operatorClient.SignInAsync("opal", "a-long-enough-password");
        var anonymous = new ApiTestClient(server);

        using var asOperator = await SetAsync(operatorClient, Password);
        using var asAnonymous = await SetAsync(anonymous, Password);
        using var withoutToken = await admin.PutAsync(BackupPassword, new { csrf_token = "wrong", password = Password, confirm_password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.False((await Json(await admin.GetAsync(Backups)))["password_set"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_backup_made_before_a_password_is_set_holds_no_secret_and_says_so_and_a_later_one_holds_them_locked()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        await AddAlertAsync(admin);

        var before = await BackUpAsync(admin);
        var beforeText = await DownloadAsync(admin, before);
        using var set = await SetAsync(admin, Password);
        var after = await BackUpAsync(admin);
        var afterText = await DownloadAsync(admin, after);

        Assert.False(before["secrets_sealed"]!.GetValue<bool>());
        Assert.DoesNotContain("sealed_secrets", beforeText, StringComparison.Ordinal);
        Assert.DoesNotContain(AlertToken, beforeText, StringComparison.Ordinal);
        Assert.True(after["secrets_sealed"]!.GetValue<bool>());
        Assert.Contains("\"sealed_secrets\"", afterText, StringComparison.Ordinal);
        Assert.DoesNotContain(AlertToken, afterText, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, afterText, StringComparison.Ordinal);
        var listed = (await Json(await admin.GetAsync(Backups)))["items"]!.AsArray();
        Assert.Equal([true, false], listed.Select(item => item!["secrets_sealed"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Restoring_a_locked_backup_needs_its_password_and_a_wrong_one_changes_nothing()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        await AddAlertAsync(admin);
        using var set = await SetAsync(admin, Password);
        var locked = await DownloadAsync(admin, await BackUpAsync(admin));
        using var removed = await admin.SendAsync(HttpMethod.Delete, $"{Channels}/1", headers: new Dictionary<string, string> { ["X-CSRF-Token"] = await admin.CsrfAsync() });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await Json(await admin.GetAsync(Channels)))["items"]!.AsArray());

        using var none = await RestoreAsync(admin, locked, null);
        using var wrong = await RestoreAsync(admin, locked, "not the password");

        Assert.Equal((HttpStatusCode.BadRequest, "This backup holds secrets locked with a backup password. Enter the backup password to restore it."), (none.StatusCode, await Detail(none)));
        Assert.Equal((HttpStatusCode.BadRequest, "That is not the backup password this backup was made with. Nothing was changed."), (wrong.StatusCode, await Detail(wrong)));
        Assert.Empty((await Json(await admin.GetAsync(Channels)))["items"]!.AsArray());

        using var right = await RestoreAsync(admin, locked, Password);

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Single((await Json(await admin.GetAsync(Channels)))["items"]!.AsArray());
    }

    [Fact]
    public async Task A_backup_without_locked_secrets_restores_with_no_password()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        var plain = await DownloadAsync(admin, await BackUpAsync(admin));

        using var restored = await RestoreAsync(admin, plain, null);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task The_password_and_the_secrets_are_not_in_the_server_log()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        await AddAlertAsync(admin);
        using var set = await SetAsync(admin, Password);
        var locked = await DownloadAsync(admin, await BackUpAsync(admin));
        using var wrong = await RestoreAsync(admin, locked, "not the password");
        using var weak = await SetAsync(admin, "short");

        var log = await (await admin.GetAsync("/api/v1/suite/logs/download")).Content.ReadAsStringAsync();

        Assert.All(new[] { Password, "not the password", AlertToken }, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
    }
}
