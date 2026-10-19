using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>Media managers and alerts leave in a backup without their secrets, and come back switched off.</summary>
public sealed class ConfigurationBundleConnectionsTests : IDisposable
{
    private readonly StoreFixture _source = new();
    private readonly StoreFixture _target = new();
    private readonly ITimeZoneResolver _zones = new IanaTimeZoneResolver();
    private readonly MediaManagerConnectionStore _managers = new();
    private readonly ConfigurationBundleStore _bundle;

    public ConfigurationBundleConnectionsTests()
    {
        _bundle = new(new SuiteSettingsStore(new AuthStore()), BackupParts.Connections(managers: _managers));
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    private static Task SeedSonarrAsync(StoreFixture store, string apiKey = "source-key-ciphertext") =>
        store.Execute(
            "INSERT INTO media_manager_connections (id, kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext) " +
            $"VALUES (7, 'sonarr', 'Sonarr', 1, 'http://sonarr:8989', '{apiKey}', 'secret-ciphertext')");

    private static Task SeedDiscordAlertAsync(StoreFixture store) =>
        store.Execute(
            "INSERT INTO notification_channels (label, provider, url, events_json, enabled) " +
            "VALUES ('Failures', 'discord', 'https://discord.com/api/webhooks/1/token', '[\"job_failed\"]', 1)");

    private Task<long> InstallManagerAsync(string kind, string baseUrl) =>
        _target.WithUnitOfWork(uow => _managers.InsertAsync(uow, kind, enabled: true, baseUrl, apiKeyCiphertext: "target-key-ciphertext"));

    private static WireObject BundledManager(string kind, string? name, string baseUrl)
    {
        var manager = new WireObject().Set("kind", kind).Set("base_url", baseUrl);
        return name is null ? manager : manager.Set("name", name);
    }

    private async Task<WireObject> BundleOfManagersAsync(params WireObject[] managers)
    {
        var bundle = await ExportAsync();
        bundle.Set("media_manager_connections", new WireArray([.. managers]));
        return bundle;
    }

    private Task<WireObject> ExportAsync() => _source.WithUnitOfWork(_bundle.BuildAsync, commit: false);

    private Task<bool> RestoreAsync(WireObject bundle) => _target.WithUnitOfWork(async uow =>
    {
        await _bundle.ApplyAsync(uow, bundle, _zones, _target.Options.WeirHome);
        return true;
    });

    [Fact]
    public async Task An_export_carries_each_media_manager_without_its_api_key_or_webhook_secret()
    {
        await SeedSonarrAsync(_source);

        var bundle = await ExportAsync();

        var connection = (WireObject)((WireArray)bundle["media_manager_connections"]).Items.Single();
        Assert.Equal("Sonarr", ((WireString)connection["name"]).Value);
        Assert.Equal("http://sonarr:8989", ((WireString)connection["base_url"]).Value);
        Assert.DoesNotContain(connection.Keys, key => key.Contains("key", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_export_carries_each_alert_without_its_address()
    {
        await SeedDiscordAlertAsync(_source);

        var bundle = await ExportAsync();

        var alert = (WireObject)((WireArray)bundle["notification_channels"]).Items.Single();
        Assert.Equal("Failures", ((WireString)alert["label"]).Value);
        Assert.False(alert.ContainsKey("url"));
        Assert.DoesNotContain("discord.com", WireJsonWriter.Dumps(bundle, WireJsonFormat.IndentedSorted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_adds_a_missing_media_manager_switched_off_and_without_secrets()
    {
        await SeedSonarrAsync(_source);
        var bundle = await ExportAsync();

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar(
            "SELECT count(*) FROM media_manager_connections WHERE name = 'Sonarr on sonarr' AND kind = 'sonarr' AND base_url = 'http://sonarr:8989' " +
            "AND enabled = 0 AND api_key_ciphertext IS NULL AND webhook_secret_ciphertext IS NULL"));
    }

    [Fact]
    public async Task Restoring_keeps_a_media_manager_this_install_already_has_with_its_key()
    {
        await SeedSonarrAsync(_source);
        await SeedSonarrAsync(_target, apiKey: "target-key-ciphertext");
        var bundle = await ExportAsync();

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
        Assert.Equal(1, await _target.Scalar(
            "SELECT count(*) FROM media_manager_connections WHERE enabled = 1 AND api_key_ciphertext = 'target-key-ciphertext'"));
    }

    [Fact]
    public async Task Restoring_adds_a_missing_alert_switched_off_and_without_an_address()
    {
        await SeedDiscordAlertAsync(_source);
        var bundle = await ExportAsync();

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar(
            "SELECT count(*) FROM notification_channels WHERE label = 'Failures' AND provider = 'discord' AND url = '' " +
            "AND enabled = 0 AND events_json LIKE '%job_failed%'"));
    }

    [Fact]
    public async Task A_restored_library_found_through_a_media_manager_points_at_that_manager_on_this_install()
    {
        await SeedSonarrAsync(_source);
        await _source.Execute("UPDATE libraries SET discovered_from_connection_id = 7 WHERE name = 'TV'");
        var bundle = await ExportAsync();

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar(
            "SELECT count(*) FROM libraries JOIN media_manager_connections ON media_manager_connections.id = libraries.discovered_from_connection_id " +
            "WHERE libraries.name = 'TV' AND media_manager_connections.name = 'Sonarr on sonarr'"));
    }

    [Fact]
    public async Task A_backup_with_an_unknown_kind_of_media_manager_is_refused_in_plain_words()
    {
        var bundle = await ExportAsync();
        bundle.Set("media_manager_connections", new WireArray([new WireObject().Set("kind", "plex").Set("name", "Living room")]));

        var refused = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(bundle));

        Assert.Contains("Living room", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_with_a_media_manager_address_that_will_not_work_refuses_the_whole_restore()
    {
        await SeedSonarrAsync(_source);
        var bundle = await ExportAsync();
        bundle.Set(
            "media_manager_connections",
            new WireArray([new WireObject().Set("kind", "sonarr").Set("name", "Living room").Set("base_url", "not-a-url")]));

        var refused = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(bundle));

        Assert.Contains("Living room", refused.Message, StringComparison.Ordinal);
        Assert.Contains("will not use", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_saved_under_a_typed_name_matches_the_connection_with_the_same_kind_and_address()
    {
        await InstallManagerAsync("sonarr", "http://sonarr:8989");
        var bundle = await BundleOfManagersAsync(BundledManager("sonarr", "Living room TV", "http://sonarr:8989"));

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_connection_matches_the_same_address_written_in_another_case()
    {
        await InstallManagerAsync("radarr", "http://NAS:7878");
        var bundle = await BundleOfManagersAsync(BundledManager("radarr", "Movies", "http://nas:7878"));

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_connection_with_another_address_or_kind_is_added_and_named_after_it()
    {
        await InstallManagerAsync("radarr", "http://nas:7878");
        var bundle = await BundleOfManagersAsync(
            BundledManager("radarr", "Movies 4K", "http://nas:7879"),
            BundledManager("sonarr", "Movies", "http://nas:7878"));

        await RestoreAsync(bundle);

        Assert.Equal(
            ["Radarr on nas (7878)", "Radarr on nas (7879)", "Sonarr on nas"],
            await _target.WithUnitOfWork(async uow => (await _managers.ListAsync(uow)).Select(row => row.Name).Order().ToList(), commit: false));
    }

    [Fact]
    public async Task A_backup_connection_with_no_address_matches_by_its_saved_name()
    {
        await _target.WithUnitOfWork(uow => _managers.InsertAsync(uow, "native", enabled: true, baseUrl: string.Empty, apiKeyCiphertext: null));
        var bundle = await BundleOfManagersAsync(BundledManager("native", "Media manager", string.Empty), BundledManager("native", "Home", string.Empty));

        await RestoreAsync(bundle);

        Assert.Equal(2, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_connection_without_a_saved_name_is_restored()
    {
        var bundle = await BundleOfManagersAsync(BundledManager("sonarr", null, "http://sonarr:8989"));

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections WHERE name = 'Sonarr on sonarr'"));
    }

    [Fact]
    public async Task An_export_carries_each_media_managers_nickname()
    {
        await SeedSonarrAsync(_source);
        await _source.Execute("UPDATE media_manager_connections SET nickname = '4K' WHERE id = 7");

        var bundle = await ExportAsync();

        var connection = (WireObject)((WireArray)bundle["media_manager_connections"]).Items.Single();
        Assert.Equal("4K", ((WireString)connection["nickname"]).Value);
    }

    [Fact]
    public async Task Restoring_adds_a_missing_media_manager_with_its_nickname()
    {
        var bundle = await BundleOfManagersAsync(BundledManager("radarr", null, "http://nas:7878").Set("nickname", "  4K  "));

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections WHERE base_url = 'http://nas:7878' AND nickname = '4K'"));
    }

    [Fact]
    public async Task Restoring_leaves_the_nickname_of_a_media_manager_this_install_already_has()
    {
        await InstallManagerAsync("radarr", "http://nas:7878");
        await _target.Execute("UPDATE media_manager_connections SET nickname = 'Kids'");
        var bundle = await BundleOfManagersAsync(BundledManager("radarr", null, "http://nas:7878").Set("nickname", "4K"));

        await RestoreAsync(bundle);

        Assert.Equal(1, await _target.Scalar("SELECT count(*) FROM media_manager_connections WHERE nickname = 'Kids'"));
    }

    [Fact]
    public async Task A_backup_with_a_nickname_longer_than_a_person_could_type_is_refused()
    {
        var bundle = await BundleOfManagersAsync(BundledManager("radarr", null, "http://nas:7878").Set("nickname", new string('a', 31)));

        var refused = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(bundle));

        Assert.Contains("nickname", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_backup_with_an_alert_label_over_two_hundred_and_fifty_five_characters_is_refused()
    {
        var bundle = await ExportAsync();
        bundle.Set(
            "notification_channels",
            new WireArray([new WireObject().Set("label", new string('a', 256)).Set("provider", "discord").Set("events", new WireArray([]))]));

        var refused = await Assert.ThrowsAsync<WireValueException>(() => RestoreAsync(bundle));

        Assert.Contains("too long", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _target.Scalar("SELECT count(*) FROM notification_channels"));
    }
}
