using Microsoft.Data.Sqlite;
using Weir.Core.Settings;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>The <c>suite_settings</c> singleton row: reading it, creating it with defaults, and updating it.</summary>
public sealed class SuiteSettingsStore
{
    private const string Columns =
        "setup_wizard_state, app_timezone, log_retention_days, activity_retention_days, " +
        "direct_play_devices, configuration_backup_enabled, configuration_backup_interval_hours, configuration_backup_preferred_time, " +
        "configuration_backup_last_run_at, processing_paused, processing_paused_until, scan_while_paused, updated_at";

    private readonly AuthStore _users;
    private readonly DataChangePublisher? _changes;

    /// <summary>Once an update commits, <paramref name="changes"/> tells every open screen that Weir's settings changed.</summary>
    public SuiteSettingsStore(AuthStore users, DataChangePublisher? changes = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _changes = changes;
    }

    public Task<SuiteSettingsRecord?> GetAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync($"SELECT {Columns} FROM suite_settings WHERE suite_settings.id = 1", Read);
    }

    /// <summary>The row, created with its defaults first when it does not exist yet.</summary>
    public async Task<SuiteSettingsRecord> EnsureAsync(UnitOfWork uow)
    {
        var row = await GetAsync(uow).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        var users = await _users.CountUsersAsync(uow).ConfigureAwait(false);
        await uow.ExecuteAsync(
            "INSERT INTO suite_settings (id, setup_wizard_state, app_timezone, log_retention_days, " +
            "activity_retention_days, configuration_backup_enabled, configuration_backup_interval_hours, configuration_backup_preferred_time, " +
            "configuration_backup_last_run_at) VALUES (1, $wizard, 'UTC', 30, 90, 0, 24, '02:00', NULL)",
            ("$wizard", SuiteSettingsRules.DefaultSetupWizardState(users))).ConfigureAwait(false);
        // The row was inserted just above, in the same transaction.
        return (await GetAsync(uow).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Write the columns that differ between <paramref name="before"/> and <paramref name="after"/>, bumping
    /// <c>updated_at</c> only when anything changed.
    /// </summary>
    public async Task UpdateAsync(UnitOfWork uow, SuiteSettingsRecord before, SuiteSettingsRecord after)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var sets = new List<string>();
        var parameters = new List<(string, object?)>();
        void Compare<T>(string column, T left, T right, Func<T, object?> toDb)
        {
            if (!EqualityComparer<T>.Default.Equals(left, right))
            {
                sets.Add($"{column}=${column}");
                parameters.Add(($"${column}", toDb(right)));
            }
        }

        Compare("setup_wizard_state", before.SetupWizardState, after.SetupWizardState, v => v);
        Compare("app_timezone", before.AppTimezone, after.AppTimezone, v => v);
        Compare("log_retention_days", before.LogRetentionDays, after.LogRetentionDays, v => v);
        Compare("activity_retention_days", before.ActivityRetentionDays, after.ActivityRetentionDays, v => v);
        Compare("configuration_backup_enabled", before.ConfigurationBackupEnabled, after.ConfigurationBackupEnabled, v => v ? 1 : 0);
        Compare("configuration_backup_interval_hours", before.ConfigurationBackupIntervalHours, after.ConfigurationBackupIntervalHours, v => v);
        Compare("configuration_backup_preferred_time", before.ConfigurationBackupPreferredTime, after.ConfigurationBackupPreferredTime, v => v);
        Compare("configuration_backup_last_run_at", before.ConfigurationBackupLastRunAt, after.ConfigurationBackupLastRunAt, v => SqliteValues.ToSqlite(v));
        Compare("processing_paused", before.ProcessingPaused, after.ProcessingPaused, v => v ? 1 : 0);
        Compare("processing_paused_until", before.ProcessingPausedUntil, after.ProcessingPausedUntil, v => SqliteValues.ToSqlite(v));
        Compare("scan_while_paused", before.ScanWhilePaused, after.ScanWhilePaused, v => v ? 1 : 0);
        Compare("direct_play_devices", before.DirectPlayDevices, after.DirectPlayDevices, v => v);
        if (sets.Count == 0)
        {
            return;
        }

        sets.Add("updated_at=CURRENT_TIMESTAMP");
        await uow.ExecuteAsync($"UPDATE suite_settings SET {string.Join(", ", sets)} WHERE suite_settings.id = 1", [.. parameters]).ConfigureAwait(false);

        // The pause (SuitePauseService) and the chosen playback devices have topics of their own, and no screen reads them with the settings.
        var apartFromOwnTopics = before with
        {
            ProcessingPaused = after.ProcessingPaused,
            ProcessingPausedUntil = after.ProcessingPausedUntil,
            ScanWhilePaused = after.ScanWhilePaused,
            DirectPlayDevices = after.DirectPlayDevices,
        };
        if (before.DirectPlayDevices != after.DirectPlayDevices)
        {
            _changes?.PublishOnCommit(uow, DataTopics.DirectPlayDevices);
        }

        if (apartFromOwnTopics != after)
        {
            _changes?.PublishOnCommit(uow, DataTopics.Settings);
        }
    }

    private static SuiteSettingsRecord Read(SqliteDataReader reader) => new()
    {
        SetupWizardState = SqliteValues.GetString(reader, 0),
        AppTimezone = SqliteValues.GetString(reader, 1),
        LogRetentionDays = SqliteValues.GetInt64(reader, 2),
        ActivityRetentionDays = SqliteValues.GetInt64(reader, 3),
        DirectPlayDevices = SqliteValues.GetString(reader, 4),
        ConfigurationBackupEnabled = SqliteValues.GetBool(reader, 5),
        ConfigurationBackupIntervalHours = SqliteValues.GetInt64(reader, 6),
        ConfigurationBackupPreferredTime = SqliteValues.GetString(reader, 7),
        ConfigurationBackupLastRunAt = SqliteValues.GetDateTimeOrNull(reader, 8),
        ProcessingPaused = SqliteValues.GetBool(reader, 9),
        ProcessingPausedUntil = SqliteValues.GetDateTimeOrNull(reader, 10),
        ScanWhilePaused = SqliteValues.GetBool(reader, 11),
        UpdatedAt = SqliteValues.GetDateTime(reader, 12),
    };
}
