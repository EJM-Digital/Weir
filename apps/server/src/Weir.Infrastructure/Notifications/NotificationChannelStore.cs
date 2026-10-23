using Microsoft.Data.Sqlite;
using Weir.Core.Notifications;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Notifications;

/// <summary>The <c>notification_channels</c> table.</summary>
public sealed class NotificationChannelStore
{
    private const string Columns = "id, label, provider, url, events_json, enabled, created_at, updated_at";

    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once a write commits, that the alert channels changed, so every open screen reads them again.</param>
    public NotificationChannelStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public Task<List<NotificationChannelRecord>> ListAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync($"SELECT {Columns} FROM notification_channels ORDER BY notification_channels.id", Read);
    }

    public Task<NotificationChannelRecord?> GetAsync(UnitOfWork uow, long id)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync($"SELECT {Columns} FROM notification_channels WHERE notification_channels.id = $id", Read, ("$id", id));
    }

    /// <summary>The enabled channels subscribed to <paramref name="jobEvent"/>.</summary>
    public async Task<List<NotificationChannelRecord>> ForEventAsync(UnitOfWork uow, string jobEvent)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync($"SELECT {Columns} FROM notification_channels WHERE notification_channels.enabled IS 1", Read).ConfigureAwait(false);
        return [.. rows.Where(row => NotificationRules.ParseEvents(row.EventsJson).Contains(jobEvent, StringComparer.Ordinal))];
    }

    /// <summary>Inserts a channel (validation is the caller's).</summary>
    public async Task<NotificationChannelRecord> CreateAsync(UnitOfWork uow, string label, string provider, string url, IReadOnlyList<string> events, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var id = await uow.ExecuteScalarWriteAsync(
            "INSERT INTO notification_channels (label, provider, url, events_json, enabled) VALUES ($label, $provider, $url, $events, $enabled) RETURNING id",
            ("$label", label.Trim()),
            ("$provider", provider),
            ("$url", url),
            ("$events", NotificationRules.SerializeEvents(events)),
            ("$enabled", enabled ? 1 : 0)).ConfigureAwait(false);
        Changed(uow);
        // Read back inside the transaction that inserted it, so the row is there.
        return (await GetAsync(uow, Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false))!;
    }

    /// <summary>Updates an existing channel row.</summary>
    public async Task<NotificationChannelRecord> UpdateAsync(
        UnitOfWork uow, NotificationChannelRecord row, string label, string provider, string url, IReadOnlyList<string> events, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var sets = new List<string>();
        var parameters = new List<(string, object?)> { ("$id", row.Id) };
        void Set(string column, bool changed, object value)
        {
            if (changed)
            {
                sets.Add($"{column}=${column}");
                parameters.Add(($"${column}", value));
            }
        }

        var trimmed = label.Trim();
        var eventsJson = NotificationRules.SerializeEvents(events);
        Set("label", row.Label != trimmed, trimmed);
        Set("provider", row.Provider != provider, provider);
        Set("url", row.Url != url, url);
        Set("events_json", row.EventsJson != eventsJson, eventsJson);
        Set("enabled", row.Enabled != enabled, enabled ? 1 : 0);
        if (sets.Count > 0)
        {
            sets.Add("updated_at=CURRENT_TIMESTAMP");
            await uow.ExecuteAsync(
                $"UPDATE notification_channels SET {string.Join(", ", sets)} WHERE notification_channels.id = $id",
                [.. parameters]).ConfigureAwait(false);
            Changed(uow);
        }

        // Read back inside the transaction that updated it, so the row is there.
        return (await GetAsync(uow, row.Id).ConfigureAwait(false))!;
    }

    public async Task<int> DeleteAsync(UnitOfWork uow, long id)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var removed = await uow.ExecuteAsync("DELETE FROM notification_channels WHERE notification_channels.id = $id", ("$id", id)).ConfigureAwait(false);
        if (removed > 0)
        {
            Changed(uow);
        }

        return removed;
    }

    private void Changed(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.Settings);

    private static NotificationChannelRecord Read(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetString(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetString(reader, 3),
        SqliteValues.GetString(reader, 4),
        SqliteValues.GetBool(reader, 5),
        SqliteValues.GetDateTime(reader, 6),
        SqliteValues.GetDateTime(reader, 7));
}
