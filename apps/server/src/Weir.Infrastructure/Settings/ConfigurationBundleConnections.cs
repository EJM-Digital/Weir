using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Notifications;
using Weir.Core.Security;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>
/// The media managers and alerts in a configuration bundle (#693).
/// </summary>
/// <remarks>
/// A bundle is a plain file people keep and copy around, so its readable part never carries a secret: no API key, no
/// webhook secret, and no alert address (a Discord or webhook address is itself the credential). The secrets travel only
/// in the sealed section a backup password locks (<see cref="ExportSecretsAsync"/>). A restore adds the ones this install
/// does not have yet, switched off, with their secrets only when the sealed section was opened, and leaves the ones it
/// already has, secrets included, exactly as they are.
/// </remarks>
public sealed class ConfigurationBundleConnections
{
    public const string MediaManagersSection = "media_manager_connections";
    public const string AlertsSection = "notification_channels";

    private const string SecretMediaManagers = "media_managers";
    private const string SecretDownloadClients = "download_clients";
    private const string SecretAlerts = "alerts";

    // Matches the create/update request model (NotificationEndpoints): a restored row must fit the same
    // column a hand-typed one does.
    private const int AlertLabelMaxLength = 255;

    private readonly NotificationChannelStore _channels;
    private readonly MediaManagerConnectionStore _connections;
    private readonly DownloadClientConnectionStore _downloadClients;
    private readonly CredentialCipher _cipher;

    public ConfigurationBundleConnections(
        NotificationChannelStore channels, MediaManagerConnectionStore connections, DownloadClientConnectionStore downloadClients, CredentialCipher cipher)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _downloadClients = downloadClients ?? throw new ArgumentNullException(nameof(downloadClients));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    public async Task<WireArray> ExportMediaManagersAsync(UnitOfWork uow)
    {
        var connections = await _connections.ListAsync(uow).ConfigureAwait(false);
        return new WireArray(connections.Select(connection => (WireValue)new WireObject()
            .Set("id", connection.Id)
            .Set("kind", connection.Kind)
            .Set("name", connection.Name)
            .Set("nickname", connection.Nickname)
            .Set("enabled", connection.Enabled)
            .Set("base_url", connection.BaseUrl)));
    }

    public async Task<WireArray> ExportAlertsAsync(UnitOfWork uow)
    {
        var channels = await _channels.ListAsync(uow).ConfigureAwait(false);
        return new WireArray(channels.Select(channel => (WireValue)new WireObject()
            .Set("label", channel.Label)
            .Set("provider", channel.Provider)
            .Set("events", new WireArray(NotificationRules.ParseEvents(channel.EventsJson).Select(e => (WireValue)new WireString(e))))
            .Set("enabled", channel.Enabled)));
    }

    /// <summary>
    /// Every secret Weir holds for its connections, in the clear, for the sealed section of a backup: the media managers' API keys
    /// and webhook secrets, the download clients (all of each, since a backup's readable part does not list them) and the alerts'
    /// addresses. A secret Weir can no longer open is left out.
    /// </summary>
    public async Task<WireObject> ExportSecretsAsync(UnitOfWork uow)
    {
        var managers = await _connections.ListAsync(uow).ConfigureAwait(false);
        var clients = await _downloadClients.ListAsync(uow).ConfigureAwait(false);
        var channels = await _channels.ListAsync(uow).ConfigureAwait(false);
        return new WireObject()
            .Set(SecretMediaManagers, new WireArray(managers.Select(connection => (WireValue)new WireObject()
                .Set("id", connection.Id)
                .Set("api_key", _cipher.Decrypt(connection.ApiKeyCiphertext))
                .Set("webhook_secret", _cipher.Decrypt(connection.WebhookSecretCiphertext)))))
            .Set(SecretDownloadClients, new WireArray(clients.Select(client => (WireValue)new WireObject()
                .Set("kind", client.Kind)
                .Set("nickname", client.Nickname)
                .Set("base_url", client.BaseUrl)
                .Set("username", client.Username)
                .Set("password", _cipher.Decrypt(client.PasswordCiphertext))
                .Set("api_key", _cipher.Decrypt(client.ApiKeyCiphertext)))))
            .Set(SecretAlerts, new WireArray(channels.Select(channel => (WireValue)new WireObject()
                .Set("label", channel.Label)
                .Set("provider", channel.Provider)
                .Set("url", channel.Url))));
    }

    /// <summary>
    /// Adds the bundle's media managers this install does not have. A bundle's connection is the one here with the
    /// same kind and address. Its saved name is compared only for a connection with no address, because a name is
    /// derived from the address (#826) and a bundle can carry a typed one. Returns each exported connection id
    /// mapped to the id it has here, or <see langword="null"/> when the bundle has no media managers section, so
    /// the libraries that name a connection can be pointed at the right row.
    /// </summary>
    public async Task<Dictionary<long, long>?> RestoreMediaManagersAsync(UnitOfWork uow, WireObject bundle, WireObject? secrets = null)
    {
        if (bundle.Get(MediaManagersSection) is not WireArray rows)
        {
            return null;
        }

        var known = (await _connections.ListAsync(uow).ConfigureAwait(false))
            .Select(connection => new KnownConnection(connection.Id, connection.Kind, connection.BaseUrl, connection.Name))
            .ToList();
        var restoredIds = new Dictionary<long, long>();
        foreach (var row in rows.Items)
        {
            var data = row as WireObject ?? throw new WireValueException("This backup's media managers are not in a form Weir can read.");
            var kind = RequiredText(data, "kind");
            var savedName = OptionalText(data, "name").Trim();
            var described = savedName.Length > 0 ? savedName : MediaManagerKinds.ProductLabel(kind);
            if (!MediaManagerKinds.All.Contains(kind, StringComparer.Ordinal))
            {
                throw new WireValueException($"This backup has a media manager, {described}, of a kind this version of Weir does not support.");
            }

            var baseUrl = ValidateRestoredBaseUrl(described, OptionalText(data, "base_url"));
            var nickname = ValidateRestoredNickname(described, data);
            var match = known.Find(connection => connection.IsSameAs(kind, baseUrl, savedName));
            long id;
            if (match is null)
            {
                var sealedSecrets = SecretsFor(secrets, data);
                var apiKey = OptionalText(sealedSecrets, "api_key");
                id = await _connections.InsertAsync(
                    uow, kind, enabled: false, baseUrl, apiKey.Length > 0 ? _cipher.Encrypt(apiKey) : null, nickname: nickname).ConfigureAwait(false);
                var webhookSecret = OptionalText(sealedSecrets, "webhook_secret");
                if (webhookSecret.Length > 0)
                {
                    await _connections.UpdateColumnsAsync(uow, id, [("webhook_secret_ciphertext", _cipher.Encrypt(webhookSecret))]).ConfigureAwait(false);
                }

                known.Add(new KnownConnection(id, kind, baseUrl, savedName));
            }
            else
            {
                id = match.Id;
            }

            if (TryReadId(data.Get("id"), out var exportedId))
            {
                restoredIds[exportedId] = id;
            }
        }

        return restoredIds;
    }

    /// <summary>
    /// Adds the download clients the sealed section holds that this install does not have (matched by kind and address), switched
    /// off, with their passwords and API keys. A backup with no sealed section has none to add.
    /// </summary>
    public async Task RestoreDownloadClientsAsync(UnitOfWork uow, WireObject? secrets)
    {
        if (secrets?.Get(SecretDownloadClients) is not WireArray rows)
        {
            return;
        }

        var known = (await _downloadClients.ListAsync(uow).ConfigureAwait(false))
            .Select(client => (client.Kind, BaseUrl: client.BaseUrl.ToUpperInvariant()))
            .ToHashSet();
        foreach (var row in rows.Items)
        {
            var data = row as WireObject ?? throw new WireValueException("This backup's download clients are not in a form Weir can read.");
            var kind = RequiredText(data, "kind");
            if (!DownloadClientKinds.All.Contains(kind, StringComparer.Ordinal))
            {
                throw new WireValueException($"This backup has a download client, {DownloadClientKinds.ProductLabel(kind)}, of a kind this version of Weir does not support.");
            }

            var baseUrl = ValidateRestoredDownloadClientAddress(DownloadClientKinds.ProductLabel(kind), OptionalText(data, "base_url"));
            if (!known.Add((kind, baseUrl.ToUpperInvariant())))
            {
                continue;
            }

            var nickname = ConnectionNicknames.Normalize(OptionalText(data, "nickname"));
            if (!ConnectionNicknames.Fits(nickname))
            {
                throw new WireValueException($"This backup has a download client, {DownloadClientKinds.ProductLabel(kind)}, with a nickname that is too long for Weir to store.");
            }

            var username = OptionalText(data, "username");
            var password = OptionalText(data, "password");
            var apiKey = OptionalText(data, "api_key");
            await _downloadClients.InsertAsync(
                uow, kind, enabled: false, baseUrl, username.Length > 0 ? username : null,
                password.Length > 0 ? _cipher.Encrypt(password) : null, apiKey.Length > 0 ? _cipher.Encrypt(apiKey) : null, nickname).ConfigureAwait(false);
        }
    }

    /// <summary>Adds the bundle's alerts this install does not have (matched by label and provider), with their addresses when the sealed section was opened.</summary>
    public async Task RestoreAlertsAsync(UnitOfWork uow, WireObject bundle, WireObject? secrets = null)
    {
        if (bundle.Get(AlertsSection) is not WireArray rows)
        {
            return;
        }

        var existing = (await _channels.ListAsync(uow).ConfigureAwait(false))
            .Select(channel => (channel.Label, channel.Provider))
            .ToHashSet();
        foreach (var row in rows.Items)
        {
            var data = row as WireObject ?? throw new WireValueException("This backup's alerts are not in a form Weir can read.");
            var label = RequiredText(data, "label", AlertLabelMaxLength).Trim();
            var provider = RequiredText(data, "provider");
            if (!NotificationRules.SupportedProviders.Contains(provider, StringComparer.Ordinal))
            {
                throw new WireValueException($"This backup has an alert, {label}, of a kind this version of Weir does not support.");
            }

            if (!existing.Add((label, provider)))
            {
                continue;
            }

            var events = data.Get("events") is WireArray list
                ? list.Items.OfType<WireString>().Select(item => item.Value).Where(e => NotificationRules.SupportedEvents.Contains(e, StringComparer.Ordinal)).ToList()
                : [];
            await _channels.CreateAsync(uow, label, provider, AlertAddress(label, secrets, provider), events, enabled: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points a library row's <c>discovered_from_connection_id</c> at the connection's id on this install, or
    /// clears it when the bundle did not carry that connection.
    /// </summary>
    public static void RemapLibraryConnection(WireObject library, IReadOnlyDictionary<long, long> restoredIds)
    {
        const string column = "discovered_from_connection_id";
        var value = library.Get(column);
        if (value is not null and not WireNull)
        {
            library.Set(column, TryReadId(value, out var exportedId) && restoredIds.TryGetValue(exportedId, out var id) ? WireValue.Of(id) : WireNull.Instance);
        }
    }

    /// <summary>The sealed secrets of the bundle's media manager with this row's id, or an empty object when it has none.</summary>
    private static WireObject SecretsFor(WireObject? secrets, WireObject row)
    {
        if (secrets?.Get(SecretMediaManagers) is WireArray entries && TryReadId(row.Get("id"), out var id))
        {
            foreach (var entry in entries.Items.OfType<WireObject>())
            {
                if (TryReadId(entry.Get("id"), out var entryId) && entryId == id)
                {
                    return entry;
                }
            }
        }

        return new WireObject();
    }

    /// <summary>The address the sealed section holds for an alert, checked as a hand-typed one is, or an empty address when it holds none.</summary>
    private static string AlertAddress(string label, WireObject? secrets, string provider)
    {
        if (secrets?.Get(SecretAlerts) is not WireArray entries)
        {
            return string.Empty;
        }

        var entry = entries.Items.OfType<WireObject>()
            .FirstOrDefault(item => OptionalText(item, "label").Trim() == label && OptionalText(item, "provider") == provider);
        var url = entry is null ? string.Empty : OptionalText(entry, "url");
        if (url.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return ExternalUrlPolicy.ValidateExternalProviderUrl(url);
        }
        catch (WireValueException exception)
        {
            throw new WireValueException($"This backup has an alert, {label}, with an address Weir will not use: {exception.Message}");
        }
    }

    private static string ValidateRestoredDownloadClientAddress(string description, string rawBaseUrl)
    {
        try
        {
            return ExternalUrlPolicy.NormalizeLocalServiceBaseUrl(rawBaseUrl.Trim());
        }
        catch (WireValueException exception)
        {
            throw new WireValueException($"This backup has a download client, {description}, with an address Weir will not use: {exception.Message}");
        }
    }

    /// <summary>A connection that exists here, or was just added from the bundle, as far as matching a bundle row needs it.</summary>
    private sealed record KnownConnection(long Id, string Kind, string BaseUrl, string Name)
    {
        public bool IsSameAs(string kind, string baseUrl, string savedName) =>
            Kind == kind &&
            (baseUrl.Length > 0
                ? string.Equals(BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
                : BaseUrl.Length == 0 && string.Equals(Name, savedName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadId(WireValue? value, out long id)
    {
        id = 0;
        if (value is not WireInteger number || number.Value < long.MinValue || number.Value > long.MaxValue)
        {
            return false;
        }

        id = (long)number.Value;
        return true;
    }

    private static string RequiredText(WireObject data, string key, int? maxLength = null)
    {
        if (data.Get(key) is not WireString text || text.Value.Trim().Length == 0)
        {
            throw new WireValueException("This backup is missing part of a media manager or alert. Download a fresh backup and try again.");
        }

        if (maxLength is { } limit && text.Value.Length > limit)
        {
            throw new WireValueException($"This backup's {key} is too long for Weir to store.");
        }

        return text.Value;
    }

    private static string OptionalText(WireObject data, string key) => data.Get(key) is WireString text ? text.Value : string.Empty;

    /// <summary>The bundle row's nickname, held to the limit a hand-typed one is, so an over-long one cannot enter through a restore.</summary>
    private static string? ValidateRestoredNickname(string connectionDescription, WireObject data)
    {
        var nickname = ConnectionNicknames.Normalize(OptionalText(data, "nickname"));
        return ConnectionNicknames.Fits(nickname)
            ? nickname
            : throw new WireValueException($"This backup has a media manager, {connectionDescription}, with a nickname that is too long for Weir to store.");
    }

    /// <summary>
    /// The same address policy a hand-typed connection is held to (<see cref="MediaManagerConnectionService.ValidateBaseUrl"/>),
    /// so a bad address cannot enter through a restore instead. Refuses the whole restore, naming the connection,
    /// rather than silently dropping or blanking the address.
    /// </summary>
    private static string ValidateRestoredBaseUrl(string connectionDescription, string rawBaseUrl)
    {
        try
        {
            return MediaManagerConnectionService.ValidateBaseUrl(rawBaseUrl);
        }
        catch (MediaManagerConnectionException exception)
        {
            throw new WireValueException($"This backup has a media manager, {connectionDescription}, with an address Weir will not use: {exception.Message}");
        }
    }
}
