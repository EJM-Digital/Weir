using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Core.Time;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// With a backup password set, a backup holds the connections' secrets only locked with it; with none, it holds none. A restore
/// needs the password, refuses a wrong one before changing anything, and still takes a backup made before there was a password.
/// </summary>
public sealed class ConfigurationBackupPasswordTests : IDisposable
{
    private const string ApiKey = "sonarr-api-key-91c3d7";
    private const string WebhookSecret = "webhook-secret-5e0a22";
    private const string ClientPassword = "qbit-password-77ab10";
    private const string AlertAddress = "https://discord.com/api/webhooks/42/alert-token-3b9f";
    private const string BackupPassword = "keep this somewhere safe";
    private const string NewPassword = "a different password entirely";

    private static readonly string[] AllSecrets = [ApiKey, WebhookSecret, ClientPassword, AlertAddress, "alert-token-3b9f"];

    private readonly StoreFixture _source = new();
    private readonly StoreFixture _target = new();
    private readonly ITimeZoneResolver _zones = new IanaTimeZoneResolver();
    private readonly MediaManagerConnectionStore _managers = new();
    private readonly DownloadClientConnectionStore _clients = new();
    private readonly NotificationChannelStore _channels = new();
    private readonly BackupPasswordStore _passwords = BackupParts.Passwords();
    private readonly ConfigurationBundleStore _bundle;
    private readonly ConfigurationBackups _sourceBackups;

    public ConfigurationBackupPasswordTests()
    {
        var suite = new SuiteSettingsStore(_source.Users);
        _bundle = new ConfigurationBundleStore(suite, BackupParts.Connections(_channels, _managers, _clients));
        _sourceBackups = new ConfigurationBackups(_source.Options, _source.Clock, _zones, suite, _bundle, _passwords);
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    private async Task SeedConnectionsAsync()
    {
        var cipher = BackupParts.Cipher();
        await _source.WithUnitOfWork(async uow =>
        {
            var id = await _managers.InsertAsync(uow, "sonarr", enabled: true, "http://sonarr:8989", cipher.Encrypt(ApiKey));
            await _managers.UpdateColumnsAsync(uow, id, [("webhook_secret_ciphertext", cipher.Encrypt(WebhookSecret))]);
            await _clients.InsertAsync(uow, "qbittorrent", enabled: true, "http://qbit:8080", "admin", cipher.Encrypt(ClientPassword), null);
            await _channels.CreateAsync(uow, "Failures", "discord", AlertAddress, ["job_failed"], enabled: true);
            return true;
        });
    }

    private async Task<(ConfigurationBackupRecord Row, string Text)> BackUpAsync()
    {
        var row = await _source.WithUnitOfWork(uow => _sourceBackups.CreateAsync(uow, BackupSource.Manual));
        _source.Clock.Advance(TimeSpan.FromMinutes(1));
        return (row, await File.ReadAllTextAsync(Path.Join(_sourceBackups.Directory, row.FileName)));
    }

    private Task<bool> SetPasswordAsync(string password) => _source.WithUnitOfWork(async uow =>
    {
        await _passwords.SetAsync(uow, password);
        return true;
    });

    private Task<bool> RestoreAsync(string text, string? password) => _target.WithUnitOfWork(async uow =>
    {
        await _bundle.ApplyAsync(uow, (WireObject)WireJsonParser.Parse(text), _zones, _target.Options.WeirHome, password);
        return true;
    });

    private Task<string?> TargetColumnAsync(string column, string table) =>
        _target.ScalarText($"SELECT {column} FROM {table} ORDER BY id LIMIT 1");

    [Fact]
    public async Task A_backup_made_with_no_password_holds_no_secret_and_says_so()
    {
        await SeedConnectionsAsync();

        var (row, text) = await BackUpAsync();

        Assert.False(row.SecretsSealed);
        Assert.DoesNotContain("sealed_secrets", text, StringComparison.Ordinal);
        Assert.All(AllSecrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));
        Assert.Equal(0, await _source.Scalar("SELECT secrets_sealed FROM suite_configuration_backup"));
    }

    [Fact]
    public async Task A_backup_made_with_a_password_holds_every_secret_only_locked_and_records_it()
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);

        var (row, text) = await BackUpAsync();

        Assert.True(row.SecretsSealed);
        Assert.Contains("\"sealed_secrets\"", text, StringComparison.Ordinal);
        Assert.All(AllSecrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));
        Assert.DoesNotContain(BackupPassword, text, StringComparison.Ordinal);
        Assert.Equal(1, await _source.Scalar("SELECT secrets_sealed FROM suite_configuration_backup"));
    }

    [Fact]
    public async Task The_password_is_never_stored_and_only_a_key_locked_with_the_credentials_secret_is_kept()
    {
        await SetPasswordAsync(BackupPassword);

        var stored = await _source.ScalarText("SELECT salt || '|' || key_ciphertext FROM backup_password");

        Assert.DoesNotContain(BackupPassword, stored, StringComparison.Ordinal);
        var key = await _source.WithUnitOfWork(uow => _passwords.GetKeyAsync(uow), commit: false);
        Assert.Equal(BackupSecretsSeal.DeriveKey(BackupPassword, key!.Salt), key.Key);
        Assert.DoesNotContain(Convert.ToBase64String(key.Key), stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restore_with_the_right_password_brings_back_each_secret_on_a_switched_off_connection()
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);
        var (_, text) = await BackUpAsync();

        await RestoreAsync(text, BackupPassword);

        var cipher = BackupParts.Cipher();
        Assert.Equal(ApiKey, cipher.Decrypt(await TargetColumnAsync("api_key_ciphertext", "media_manager_connections")));
        Assert.Equal(WebhookSecret, cipher.Decrypt(await TargetColumnAsync("webhook_secret_ciphertext", "media_manager_connections")));
        Assert.Equal(ClientPassword, cipher.Decrypt(await TargetColumnAsync("password_ciphertext", "download_client_connections")));
        Assert.Equal("admin", await TargetColumnAsync("username", "download_client_connections"));
        Assert.Equal(AlertAddress, await TargetColumnAsync("url", "notification_channels"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections WHERE enabled = 1"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM download_client_connections WHERE enabled = 1"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM notification_channels WHERE enabled = 1"));
    }

    [Fact]
    public async Task A_restore_leaves_a_connection_this_install_already_has_exactly_as_it_is()
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);
        var (_, text) = await BackUpAsync();
        var existing = await _target.WithUnitOfWork(uow => _managers.InsertAsync(uow, "sonarr", enabled: true, "http://sonarr:8989", "this-installs-own-key"));

        await RestoreAsync(text, BackupPassword);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
        Assert.Equal("this-installs-own-key", await _target.ScalarText($"SELECT api_key_ciphertext FROM media_manager_connections WHERE id = {existing}"));
    }

    [Fact]
    public async Task A_wrong_password_refuses_plainly_and_changes_nothing()
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);
        var (_, text) = await BackUpAsync();
        await _target.Execute("UPDATE suite_settings SET log_retention_days = 11");

        var error = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(text, "not the password"));

        Assert.Equal("That is not the backup password this backup was made with. Nothing was changed.", error.Message);
        Assert.Equal(11, await _target.Scalar("SELECT log_retention_days FROM suite_settings"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM download_client_connections"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_restore_with_no_password_asks_for_it_and_changes_nothing(string? password)
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);
        var (_, text) = await BackUpAsync();

        var error = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(text, password));

        Assert.Equal("This backup holds secrets locked with a backup password. Enter the backup password to restore it.", error.Message);
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_made_before_there_was_a_password_still_restores_with_or_without_one_typed()
    {
        await SeedConnectionsAsync();
        var (_, text) = await BackUpAsync();

        await RestoreAsync(text, null);
        await RestoreAsync(text, "typed anyway");

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections WHERE api_key_ciphertext IS NOT NULL"));
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM download_client_connections"));
    }

    [Fact]
    public async Task Changing_the_password_locks_later_backups_with_the_new_one_and_leaves_older_ones_on_the_old_one()
    {
        await SeedConnectionsAsync();
        await SetPasswordAsync(BackupPassword);
        var (_, older) = await BackUpAsync();
        await SetPasswordAsync(NewPassword);
        var (_, newer) = await BackUpAsync();

        await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(newer, BackupPassword));
        await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(older, NewPassword));
        await RestoreAsync(older, BackupPassword);
        await RestoreAsync(newer, NewPassword);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_weak_password_is_refused_with_the_reason_and_none_is_set()
    {
        var error = await Assert.ThrowsAsync<WireValueException>(() => SetPasswordAsync("short"));

        Assert.Equal("Password must be at least 8 characters.", error.Message);
        Assert.False(await _source.WithUnitOfWork(uow => _passwords.IsSetAsync(uow), commit: false));
    }

    [Fact]
    public async Task A_password_Weir_can_no_longer_open_stops_the_backup_instead_of_leaving_the_secrets_out_unnoticed()
    {
        await SetPasswordAsync(BackupPassword);
        await _source.Execute("UPDATE backup_password SET key_ciphertext = 'garbled'");

        var error = await Assert.ThrowsAsync<WireValueException>(() => BackUpAsync());

        Assert.Equal("Weir cannot read the backup password it saved. Set the backup password again on System, Backups.", error.Message);
        Assert.Equal(0, await _source.Scalar("SELECT count(*) FROM suite_configuration_backup"));
    }
}
