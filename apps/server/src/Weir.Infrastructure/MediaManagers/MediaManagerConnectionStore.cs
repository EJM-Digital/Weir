using Microsoft.Data.Sqlite;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>One <c>media_manager_connections</c> row.</summary>
public sealed record MediaManagerConnectionRecord(
    long Id,
    string Kind,
    string Name,
    bool Enabled,
    string BaseUrl,
    string? ApiKeyCiphertext,
    string? WebhookSecretCiphertext,
    bool? LastTestOk,
    Timestamp? LastTestAt,
    string? LastTestDetail,
    bool DownloadedScanEnabled = false,
    string? Nickname = null,
    long? LastAnswerMs = null,
    Timestamp? LastUsedAt = null)
{
    /// <summary>How this connection is written in a sentence, led by its product and followed by its nickname.</summary>
    public string Label => MediaManagerKinds.LabelForConnection(Kind, Name, Nickname);

    /// <summary>The path a manager of this kind posts its webhook to.</summary>
    public string WebhookUrlPath => $"/api/v1/intake/webhook/{Kind}";

    /// <summary>
    /// With no secret of its own, this connection's kind still accepts an unsigned webhook (upgrades must not
    /// break), so the web app needs a way to tell the operator that is happening.
    /// </summary>
    public bool AcceptsUnsignedWebhooks => string.IsNullOrEmpty(WebhookSecretCiphertext);

    /// <summary>The connection as the API returns it: secrets are reported only as saved or not.</summary>
    public WireObject ToOut() => new WireObject()
        .Set("id", Id)
        .Set("kind", Kind)
        .Set("name", Name)
        .Set("nickname", Nickname)
        .Set("enabled", Enabled)
        .Set("base_url", BaseUrl)
        .Set("api_key_is_saved", !string.IsNullOrEmpty(ApiKeyCiphertext))
        .Set("webhook_secret_is_set", !string.IsNullOrEmpty(WebhookSecretCiphertext))
        .Set("webhook_url_path", WebhookUrlPath)
        .Set(
            "unsigned_webhook_warning",
            AcceptsUnsignedWebhooks
                ? $"This connection accepts webhooks without a secret. Create a secret and add it to {Label}."
                : null)
        .Set("last_test_ok", LastTestOk is { } ok ? WireValue.Of(ok) : WireValue.Null)
        .Set("last_test_at", LastTestAt is { } at ? at.ToWireText() : null)
        .Set("last_test_detail", LastTestDetail)
        .Set("last_answer_ms", LastAnswerMs)
        .Set("last_used_at", ConnectionUsage.WireText(LastUsedAt))
        .Set("downloaded_scan_enabled", DownloadedScanEnabled);
}

/// <summary>
/// The legacy <c>arr_library_operator_settings</c> singleton that migration 0009 copied out of but did not drop.
/// </summary>
public sealed class ArrLibraryOperatorSettingsStore
{
    /// <summary>Row 1 with its defaults, created when missing.</summary>
    public async Task EnsureRowAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (await uow.ScalarAsync("SELECT id FROM arr_library_operator_settings WHERE id = 1").ConfigureAwait(false) is null or DBNull)
        {
            await uow.ExecuteAsync("INSERT INTO arr_library_operator_settings (id) VALUES (1)").ConfigureAwait(false);
        }
    }
}

/// <summary>Explicit SQL over <c>media_manager_connections</c>.</summary>
public sealed class MediaManagerConnectionStore
{
    private const string ConnectionColumns =
        "id, kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext, " +
        "last_connection_test_ok, last_connection_test_at, last_connection_test_detail, downloaded_scan_enabled, nickname, " +
        "last_answer_ms, last_used_at";

    private readonly ConnectionUsageLedger? _usage;
    private readonly DataChangePublisher? _changes;

    /// <param name="usage">Newer usage than the database holds, laid over every connection a list or lookup returns; none reads the database alone.</param>
    /// <param name="changes">Told, once a write commits, that the connections changed, so every open screen reads them again.</param>
    public MediaManagerConnectionStore(ConnectionUsageLedger? usage = null, DataChangePublisher? changes = null)
    {
        _usage = usage;
        _changes = changes;
    }

    /// <summary>Every connection, by id.</summary>
    public async Task<List<MediaManagerConnectionRecord>> ListAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync($"SELECT {ConnectionColumns} FROM media_manager_connections ORDER BY id", ReadConnection).ConfigureAwait(false);
        return [.. rows.Select(WithLiveUsage)];
    }

    /// <summary>Enabled connections by id.</summary>
    public async Task<List<MediaManagerConnectionRecord>> ListEnabledAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return await uow.QueryAsync($"SELECT {ConnectionColumns} FROM media_manager_connections WHERE enabled IS 1 ORDER BY id", ReadConnection).ConfigureAwait(false);
    }

    /// <summary>One connection, or null.</summary>
    public async Task<MediaManagerConnectionRecord?> GetAsync(UnitOfWork uow, long connectionId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var row = await uow.QuerySingleAsync($"SELECT {ConnectionColumns} FROM media_manager_connections WHERE id = $id", ReadConnection, ("$id", connectionId)).ConfigureAwait(false);
        return row is null ? null : WithLiveUsage(row);
    }

    /// <summary>The first enabled connection of a kind.</summary>
    public Task<MediaManagerConnectionRecord?> FirstEnabledForKindAsync(UnitOfWork uow, string kind)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync(
            $"SELECT {ConnectionColumns} FROM media_manager_connections WHERE kind = $kind AND enabled IS 1 ORDER BY id LIMIT 1",
            ReadConnection,
            ("$kind", kind));
    }

    /// <summary>
    /// Every enabled connection of a kind, by id. Intake authenticates an inbound webhook against whichever
    /// connection's own secret was presented, so a second connection of the same kind (a 4K Radarr alongside a
    /// 1080p one, say) can authenticate too, not only <see cref="FirstEnabledForKindAsync"/>'s row (#544 item 6).
    /// </summary>
    public Task<List<MediaManagerConnectionRecord>> ListEnabledForKindAsync(UnitOfWork uow, string kind)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            $"SELECT {ConnectionColumns} FROM media_manager_connections WHERE kind = $kind AND enabled IS 1 ORDER BY id",
            ReadConnection,
            ("$kind", kind));
    }

    /// <summary>Enabled connections that have a webhook secret saved.</summary>
    public Task<List<MediaManagerConnectionRecord>> ListEnabledWithWebhookSecretAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            $"SELECT {ConnectionColumns} FROM media_manager_connections WHERE enabled IS 1 AND webhook_secret_ciphertext IS NOT NULL",
            ReadConnection);
    }

    /// <summary>
    /// Give every connection the name derived from its kind and address (<see cref="ConnectionNaming"/>). Every write
    /// that can change a derived name does this itself; startup calls it to bring names stored before they were derived in line.
    /// </summary>
    public Task RefreshNamesAsync(UnitOfWork uow) =>
        ConnectionNameColumn.RefreshAsync(uow, "media_manager_connections", MediaManagerKinds.ProductLabel);

    /// <summary>Insert a connection; returns the new id.</summary>
    public async Task<long> InsertAsync(
        UnitOfWork uow, string kind, bool enabled, string baseUrl, string? apiKeyCiphertext, bool downloadedScanEnabled = false, string? nickname = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var id = Convert.ToInt64(
            await uow.ExecuteScalarWriteAsync(
                "INSERT INTO media_manager_connections (kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext, " +
                "last_connection_test_ok, last_connection_test_at, last_connection_test_detail, downloaded_scan_enabled, nickname) " +
                "VALUES ($kind, $name, $enabled, $base_url, $key, NULL, NULL, NULL, NULL, $scan, $nickname) RETURNING id",
                ("$kind", kind),
                ("$name", ConnectionNameColumn.NewPlaceholder()),
                ("$enabled", enabled ? 1 : 0),
                ("$base_url", baseUrl),
                ("$key", apiKeyCiphertext),
                ("$scan", downloadedScanEnabled ? 1 : 0),
                ("$nickname", nickname)).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await RefreshNamesAsync(uow).ConfigureAwait(false);
        Changed(uow);
        return id;
    }

    /// <summary>Write the changed columns and stamp <c>updated_at</c>. Nothing changed, nothing written.</summary>
    public async Task UpdateColumnsAsync(UnitOfWork uow, long connectionId, IReadOnlyList<(string Column, object? Value)> changes)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        var sets = changes.Select((change, index) => $"{change.Column} = $v{index}");
        var parameters = changes.Select((change, index) => ($"$v{index}", change.Value)).Append(("$id", (object?)connectionId)).ToArray();
        await uow.ExecuteAsync(
            $"UPDATE media_manager_connections SET {string.Join(", ", sets)}, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            parameters).ConfigureAwait(false);
        if (changes.Any(change => change.Column == "base_url"))
        {
            await RefreshNamesAsync(uow).ConfigureAwait(false);
        }

        Changed(uow);
    }

    /// <summary>Delete the connection.</summary>
    public async Task DeleteAsync(UnitOfWork uow, long connectionId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        await uow.ExecuteAsync("DELETE FROM media_manager_connections WHERE id = $id", ("$id", connectionId)).ConfigureAwait(false);
        await RefreshNamesAsync(uow).ConfigureAwait(false);
        _usage?.Forget(new ConnectionRef(ConnectionKind.MediaManager, connectionId));
        Changed(uow);
    }

    /// <summary>Save how long the last call took and when the connection was last used; a value the usage does not carry stays as it was.</summary>
    public Task<int> RecordUsageAsync(UnitOfWork uow, long connectionId, ConnectionUsage usage)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE media_manager_connections SET last_answer_ms = COALESCE($ms, last_answer_ms), last_used_at = COALESCE($at, last_used_at) WHERE id = $id",
            ("$ms", usage.AnswerMilliseconds),
            ("$at", usage.UsedAt?.ToSqlite()),
            ("$id", connectionId));
    }

    private MediaManagerConnectionRecord WithLiveUsage(MediaManagerConnectionRecord row)
    {
        if (_usage is null)
        {
            return row;
        }

        var usage = _usage.Overlay(new ConnectionRef(ConnectionKind.MediaManager, row.Id), row.LastAnswerMs, row.LastUsedAt);
        return row with { LastAnswerMs = usage.AnswerMilliseconds, LastUsedAt = usage.UsedAt };
    }

    /// <summary>The conditional test-result write: 0 when the connection was removed meanwhile.</summary>
    public async Task<int> RecordTestResultAsync(UnitOfWork uow, long connectionId, bool ok, Timestamp checkedAt, string detail)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var written = await uow.ExecuteAsync(
            "UPDATE media_manager_connections SET last_connection_test_ok = $ok, last_connection_test_at = $at, " +
            "last_connection_test_detail = $detail, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$ok", ok ? 1 : 0),
            ("$at", checkedAt.ToSqlite()),
            ("$detail", detail),
            ("$id", connectionId)).ConfigureAwait(false);
        if (written > 0)
        {
            Changed(uow);
        }

        return written;
    }

    private void Changed(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.Connections);

    private static MediaManagerConnectionRecord ReadConnection(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetString(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetBool(reader, 3),
        SqliteValues.GetString(reader, 4),
        SqliteValues.GetStringOrNull(reader, 5),
        SqliteValues.GetStringOrNull(reader, 6),
        SqliteValues.GetBoolOrNull(reader, 7),
        SqliteValues.GetDateTimeOrNull(reader, 8),
        SqliteValues.GetStringOrNull(reader, 9),
        SqliteValues.GetBool(reader, 10),
        SqliteValues.GetStringOrNull(reader, 11),
        SqliteValues.GetInt64OrNull(reader, 12),
        SqliteValues.GetDateTimeOrNull(reader, 13));
}
