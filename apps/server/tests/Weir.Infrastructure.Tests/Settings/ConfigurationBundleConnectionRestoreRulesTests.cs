using Weir.Core.Json;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// What restoring the connections of a backup does with a row it cannot use: the whole restore stops, names the connection and says
/// why in plain words, and a row it can use comes back switched off, with the secrets the backup carried.
/// </summary>
public sealed class ConfigurationBundleConnectionRestoreRulesTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly ConfigurationBundleConnections _connections = BackupParts.Connections();

    public void Dispose() => _store.Dispose();

    private static readonly string LongNickname = new('n', 31);

    private static WireObject BundleOf(string section, params WireValue[] rows) => new WireObject().Set(section, new WireArray([.. rows]));

    private static WireObject Manager(string kind = "sonarr", string baseUrl = "http://sonarr:8989") =>
        new WireObject().Set("kind", kind).Set("base_url", baseUrl);

    private static WireObject DownloadClient(string kind = "qbittorrent", string baseUrl = "http://qbit:8080") =>
        new WireObject().Set("kind", kind).Set("base_url", baseUrl);

    private static WireObject Alert(string label = "Failures", string provider = "discord") =>
        new WireObject().Set("label", label).Set("provider", provider);

    private static WireObject SecretsOf(string section, params WireValue[] rows) => new WireObject().Set(section, new WireArray([.. rows]));

    private Task<Dictionary<long, long>?> RestoreManagersAsync(WireObject bundle, WireObject? secrets = null) =>
        _store.WithUnitOfWork(uow => _connections.RestoreMediaManagersAsync(uow, bundle, secrets));

    private Task<bool> RestoreClientsAsync(WireObject? secrets) =>
        _store.WithUnitOfWork(async uow =>
        {
            await _connections.RestoreDownloadClientsAsync(uow, secrets);
            return true;
        });

    private Task<bool> RestoreAlertsAsync(WireObject bundle, WireObject? secrets = null) =>
        _store.WithUnitOfWork(async uow =>
        {
            await _connections.RestoreAlertsAsync(uow, bundle, secrets);
            return true;
        });

    private static async Task<string> RefusalAsync(Func<Task> restore) => (await Assert.ThrowsAsync<WireValueException>(restore)).Message;

    public static TheoryData<int> ConstructorArguments => new() { 0, 1, 2, 3 };

    [Theory]
    [MemberData(nameof(ConstructorArguments))]
    public void The_connections_restore_cannot_be_built_without_one_of_its_stores_or_the_cipher(int missing)
    {
        var channels = new Weir.Infrastructure.Notifications.NotificationChannelStore();
        var managers = new Weir.Infrastructure.MediaManagers.MediaManagerConnectionStore();
        var clients = new Weir.Infrastructure.MediaManagers.DownloadClientConnectionStore();
        var cipher = BackupParts.Cipher();

        Assert.Throws<ArgumentNullException>(() => new ConfigurationBundleConnections(
            missing == 0 ? null! : channels, missing == 1 ? null! : managers, missing == 2 ? null! : clients, missing == 3 ? null! : cipher));
    }

    [Fact]
    public async Task A_media_manager_row_that_is_not_an_object_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreManagersAsync(BundleOf("media_manager_connections", new WireString("Sonarr"))));

        Assert.Contains("media managers are not in a form Weir can read", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_media_manager_without_a_kind_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreManagersAsync(BundleOf("media_manager_connections", new WireObject().Set("base_url", "http://sonarr:8989"))));

        Assert.Contains("missing part of a media manager or alert", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plex", "My Plex", "My Plex")]
    [InlineData("plex", "", "Media manager")]
    public async Task A_media_manager_of_a_kind_Weir_does_not_know_is_named_in_the_refusal(string kind, string name, string expectedName)
    {
        var message = await RefusalAsync(() => RestoreManagersAsync(BundleOf("media_manager_connections", Manager(kind).Set("name", name))));

        Assert.Contains($"{expectedName}, of a kind this version of Weir does not support", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_media_manager_with_an_address_Weir_will_not_use_is_named_in_the_refusal()
    {
        var message = await RefusalAsync(() => RestoreManagersAsync(BundleOf("media_manager_connections", Manager(baseUrl: "ftp://sonarr").Set("name", "Sonarr"))));

        Assert.Contains("Sonarr, with an address Weir will not use", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_media_manager_nickname_that_is_too_long_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreManagersAsync(BundleOf("media_manager_connections", Manager().Set("name", "Sonarr").Set("nickname", LongNickname))));

        Assert.Contains("Sonarr, with a nickname that is too long", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_media_manager_is_added_switched_off_with_the_secrets_whose_exported_id_matches_and_none_from_another_id()
    {
        var bundle = BundleOf(
            "media_manager_connections",
            Manager().Set("id", 4L),
            Manager("radarr", "http://radarr:7878").Set("id", 5L),
            Manager("deluno", "http://deluno:1").Set("id", "not a number"));
        var secrets = SecretsOf("media_managers", new WireObject().Set("id", 4L).Set("api_key", "sonarr-key").Set("webhook_secret", "hook"), new WireString("noise"));

        await RestoreManagersAsync(bundle, secrets);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM media_manager_connections WHERE kind = 'sonarr' AND enabled = 0 AND api_key_ciphertext IS NOT NULL AND webhook_secret_ciphertext IS NOT NULL"));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM media_manager_connections WHERE kind = 'radarr' AND enabled = 0 AND api_key_ciphertext IS NULL"));
        Assert.Equal(3, await _store.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public async Task A_media_manager_already_here_is_not_added_again()
    {
        var bundle = BundleOf("media_manager_connections", Manager().Set("id", 4L));
        await RestoreManagersAsync(bundle);

        await RestoreManagersAsync(bundle);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM media_manager_connections"));
    }

    [Theory]
    [InlineData("plex", "http://plex:1", "does not support")]
    [InlineData("qbittorrent", "ftp://qbit", "with an address Weir will not use")]
    public async Task A_download_client_Weir_cannot_use_stops_the_restore(string kind, string baseUrl, string reason)
    {
        var message = await RefusalAsync(() => RestoreClientsAsync(SecretsOf("download_clients", DownloadClient(kind, baseUrl))));

        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_client_row_that_is_not_an_object_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreClientsAsync(SecretsOf("download_clients", new WireString("qBittorrent"))));

        Assert.Contains("download clients are not in a form Weir can read", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_client_nickname_that_is_too_long_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreClientsAsync(SecretsOf("download_clients", DownloadClient().Set("nickname", LongNickname))));

        Assert.Contains("nickname that is too long", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_clients_come_back_switched_off_once_each_and_a_backup_without_secrets_adds_none()
    {
        await RestoreClientsAsync(null);
        await RestoreClientsAsync(new WireObject());
        var secrets = SecretsOf(
            "download_clients",
            DownloadClient().Set("username", "admin").Set("password", "pw").Set("api_key", "key").Set("nickname", "Torrents"),
            DownloadClient(baseUrl: "HTTP://QBIT:8080/"),
            DownloadClient("sabnzbd", "http://sab:8080"));

        await RestoreClientsAsync(secrets);

        Assert.Equal(2, await _store.Scalar("SELECT count(*) FROM download_client_connections WHERE enabled = 0"));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM download_client_connections WHERE kind = 'qbittorrent' AND username = 'admin' AND nickname = 'Torrents' AND password_ciphertext IS NOT NULL AND api_key_ciphertext IS NOT NULL"));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM download_client_connections WHERE kind = 'sabnzbd' AND username IS NULL AND password_ciphertext IS NULL"));
    }

    [Fact]
    public async Task An_alert_row_that_is_not_an_object_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreAlertsAsync(BundleOf("notification_channels", new WireString("Failures"))));

        Assert.Contains("alerts are not in a form Weir can read", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "discord")]
    [InlineData("Failures", "")]
    public async Task An_alert_without_a_label_or_a_kind_stops_the_restore(string label, string provider)
    {
        var message = await RefusalAsync(() => RestoreAlertsAsync(BundleOf("notification_channels", Alert(label, provider))));

        Assert.Contains("missing part of a media manager or alert", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_alert_label_that_is_too_long_stops_the_restore()
    {
        var message = await RefusalAsync(() => RestoreAlertsAsync(BundleOf("notification_channels", Alert(new string('x', 256)))));

        Assert.Contains("label is too long", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_alert_of_a_kind_Weir_does_not_know_is_named_in_the_refusal()
    {
        var message = await RefusalAsync(() => RestoreAlertsAsync(BundleOf("notification_channels", Alert("Pager", "pagerduty"))));

        Assert.Contains("Pager, of a kind this version of Weir does not support", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost/hook")]
    [InlineData("ftp://example.com/hook")]
    public async Task An_alert_address_Weir_will_not_use_stops_the_restore_and_names_the_alert(string url)
    {
        var secrets = SecretsOf("alerts", new WireObject().Set("label", "Failures").Set("provider", "discord").Set("url", url));

        var message = await RefusalAsync(() => RestoreAlertsAsync(BundleOf("notification_channels", Alert()), secrets));

        Assert.Contains("Failures, with an address Weir will not use", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alerts_come_back_switched_off_with_their_known_events_and_an_address_only_when_the_backup_held_one()
    {
        var bundle = BundleOf(
            "notification_channels",
            Alert("Failures").Set("events", new WireArray([new WireString("job_failed"), new WireString("made_up"), WireNull.Instance])),
            Alert("Quiet", "webhook").Set("events", "job_failed"),
            Alert("No address"),
            Alert("Failures"),
            Alert("Blank address", "webhook"));
        var secrets = SecretsOf(
            "alerts",
            new WireObject().Set("label", "Failures").Set("provider", "discord").Set("url", "https://discord.com/api/webhooks/1/token"),
            new WireObject().Set("label", "Blank address").Set("provider", "webhook").Set("url", ""),
            new WireString("noise"));

        await RestoreAlertsAsync(bundle, secrets);
        await RestoreAlertsAsync(new WireObject());

        Assert.Equal(4, await _store.Scalar("SELECT count(*) FROM notification_channels WHERE enabled = 0"));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM notification_channels WHERE label = 'Failures' AND url = 'https://discord.com/api/webhooks/1/token' AND events_json LIKE '%job_failed%' AND events_json NOT LIKE '%made_up%'"));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM notification_channels WHERE label = 'Quiet' AND events_json NOT LIKE '%job_failed%'"));
    }

    [Fact]
    public async Task Alerts_from_a_backup_with_secrets_but_none_for_alerts_come_back_without_an_address()
    {
        await RestoreAlertsAsync(BundleOf("notification_channels", Alert()), new WireObject().Set("media_managers", new WireArray()));

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM notification_channels WHERE label = 'Failures' AND url = ''"));
    }

    [Fact]
    public void A_workflow_found_through_a_restored_connection_points_at_that_connection_s_id_here_and_otherwise_at_none()
    {
        var restored = new Dictionary<long, long> { [3] = 30 };
        var mapped = new WireObject().Set("discovered_from_connection_id", 3L);
        var unmapped = new WireObject().Set("discovered_from_connection_id", 9L);
        var notAnId = new WireObject().Set("discovered_from_connection_id", "three");
        var none = new WireObject().Set("discovered_from_connection_id", WireNull.Instance);
        var absent = new WireObject();

        foreach (var library in new[] { mapped, unmapped, notAnId, none, absent })
        {
            ConfigurationBundleConnections.RemapLibraryConnection(library, restored);
        }

        Assert.Equal(30, ((WireInteger)mapped["discovered_from_connection_id"]).Value);
        Assert.Same(WireNull.Instance, unmapped["discovered_from_connection_id"]);
        Assert.Same(WireNull.Instance, notAnId["discovered_from_connection_id"]);
        Assert.Same(WireNull.Instance, none["discovered_from_connection_id"]);
        Assert.False(absent.ContainsKey("discovered_from_connection_id"));
    }
}
