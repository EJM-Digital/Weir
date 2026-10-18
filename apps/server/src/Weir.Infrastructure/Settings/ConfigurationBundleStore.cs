using Weir.Core.Json;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>
/// Export and restore of the settings rows, format version 4 only. Media managers and alerts travel without their
/// secrets; see <see cref="ConfigurationBundleConnections"/>. Restoring <c>libraries</c> and <c>rule_sets</c> lives
/// in the <c>.Libraries</c> partial, and the generic column/row IO both halves share lives in the <c>.RowIO</c>
/// partial.
/// </summary>
/// <remarks>
/// Bundles in the older format version 3 are refused rather than converted: conversion code that only runs on
/// input nobody has can rot unnoticed while sitting on the restore path for current bundles.
/// </remarks>
public sealed partial class ConfigurationBundleStore
{
    public const int FormatVersion = 4;

    private const string SuiteTable = "suite_settings";
    private const string ArrTable = "arr_library_operator_settings";
    private const string ProcessingOperatorTable = "operator_settings";
    private const string RuleSetsTable = "rule_sets";
    private const string LibrariesTable = "libraries";

    /// <summary>The space to keep free: a workflow's own now, Setup › Performance › Speed's in a backup made before that.</summary>
    private const string FreeSpaceColumn = "minimum_free_disk_space_mb";

    private readonly SuiteSettingsStore _suiteSettings;
    private readonly ConfigurationBundleConnections _connections;
    private readonly DataChangePublisher? _changes;

    private enum ColumnKind
    {
        Raw,
        Boolean,
        DateTime,
    }

    private sealed record Column(string Name, ColumnKind Kind);

    /// <summary>Once a restore commits, <paramref name="changes"/> tells every open screen that the workflows and settings it rewrote changed.</summary>
    public ConfigurationBundleStore(SuiteSettingsStore suiteSettings, ConfigurationBundleConnections connections, DataChangePublisher? changes = null)
    {
        _suiteSettings = suiteSettings ?? throw new ArgumentNullException(nameof(suiteSettings));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _changes = changes;
    }

    /// <summary>Build the export bundle from the settings rows. Throws <see cref="WireValueException"/> when a required row is missing.</summary>
    public async Task<WireObject> BuildAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
        var suite = await ReadRowsAsync(uow, SuiteTable, "WHERE id = 1").ConfigureAwait(false);
        // The export never carries the metadata provider key, encrypted or not; import already leaves the existing
        // key alone when a bundle omits it.
        if (suite.Count > 0 && suite[0] is WireObject suiteSettings)
        {
            suiteSettings.Remove("metadata_provider_key_ciphertext");
        }

        var arr = await ReadRowsAsync(uow, ArrTable, "WHERE id = 1").ConfigureAwait(false);
        var processingOperator = await ReadRowsAsync(uow, ProcessingOperatorTable, "WHERE id = 1").ConfigureAwait(false);
        var libraries = await ReadRowsAsync(uow, LibrariesTable, "ORDER BY id").ConfigureAwait(false);
        var ruleSets = await ReadRowsAsync(uow, RuleSetsTable, "ORDER BY id").ConfigureAwait(false);
        if (arr.Count == 0)
        {
            throw new WireValueException("Missing required configuration row: arr_library_operator_settings");
        }

        if (processingOperator.Count == 0)
        {
            throw new WireValueException("Missing required configuration row: operator_settings");
        }

        return new WireObject()
            .Set("format_version", FormatVersion)
            .Set("suite_settings", suite[0])
            .Set("arr_library_operator_settings", arr[0])
            .Set("operator_settings", processingOperator[0])
            .Set("rule_sets", new WireArray(ruleSets))
            .Set("libraries", new WireArray(libraries))
            .Set(ConfigurationBundleConnections.MediaManagersSection, await _connections.ExportMediaManagersAsync(uow).ConfigureAwait(false))
            .Set(ConfigurationBundleConnections.AlertsSection, await _connections.ExportAlertsAsync(uow).ConfigureAwait(false));
    }

    /// <summary>
    /// Restore a bundle into the settings rows. <see cref="WireValueException"/> becomes a 400; anything else
    /// (<see cref="WireTypeException"/>, <see cref="Microsoft.Data.Sqlite.SqliteException"/>) is a server error.
    /// </summary>
    public async Task ApplyAsync(UnitOfWork uow, WireObject bundle, ITimeZoneResolver zones, string weirHome)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(weirHome);
        var supported = bundle.Get("format_version") switch
        {
            WireInteger i => i.Value == FormatVersion,
            WireNumber f => f.Value == FormatVersion,
            _ => false,
        };
        if (!supported)
        {
            throw new WireValueException("This backup was made by a version of Weir that this one cannot restore.");
        }

        foreach (var key in new[] { SuiteTable, ArrTable, ProcessingOperatorTable })
        {
            if (!bundle.ContainsKey(key))
            {
                throw new WireValueException("This file is not a complete Weir backup.");
            }
        }

        await ApplySuiteSettingsAsync(uow, bundle[SuiteTable], zones).ConfigureAwait(false);
        await ApplySingletonAsync(uow, ArrTable, bundle[ArrTable]).ConfigureAwait(false);
        await ApplySingletonAsync(uow, ProcessingOperatorTable, bundle[ProcessingOperatorTable]).ConfigureAwait(false);
        var restoredConnectionIds = await _connections.RestoreMediaManagersAsync(uow, bundle).ConfigureAwait(false);
        await RestoreProcessingLibrariesAsync(uow, bundle, weirHome, restoredConnectionIds).ConfigureAwait(false);
        await _connections.RestoreAlertsAsync(uow, bundle).ConfigureAwait(false);
        _changes?.PublishOnCommit(uow, DataTopics.Libraries);
        _changes?.PublishOnCommit(uow, DataTopics.Settings);
    }

    private async Task ApplySuiteSettingsAsync(UnitOfWork uow, WireValue section, ITimeZoneResolver zones)
    {
        // A backup's product_display_name (Weir is named after its machine, #826) and signed_in_home_notice (no page shows one) are not read, and
        // neither are the pause and the setup wizard, which describe this install as it is now rather than how it is set up.
        var ss = section as WireObject ?? throw new WireTypeException($"The backup's {SuiteTable} section must be an object.");
        var timezone = WireConvert.Str(Required(ss, "app_timezone"));
        var logRetention = WireConvert.ToInt(Required(ss, "log_retention_days"));
        bool? backupEnabled = ss.Get("configuration_backup_enabled") is { } enabledValue and not WireNull ? enabledValue.IsTruthy : null;
        long? backupHours = null;
        var hoursValue = ss.Get("configuration_backup_interval_hours");

        // Validated in the same order as a suite settings save, so the same error is reported first:
        // timezone, log retention, then activity and interval.
        var preferredTime = ss.Get("configuration_backup_preferred_time") is WireString time ? time.Value : null;
        var normalized = SuiteSettingsRules.Normalize(new SuiteSettingsUpdate(timezone, Saturate(logRetention), ConfigurationBackupPreferredTime: preferredTime), zones);
        long? activity = null;
        if (ss.Get("activity_retention_days") is { } activityValue and not WireNull)
        {
            activity = Saturate(WireConvert.ToInt(activityValue));
            if (activity is < 0 or > 3650)
            {
                throw new WireValueException("Activity history must be kept between 0 (forever) and 3650 days.");
            }
        }

        normalized = normalized with { ActivityRetentionDays = activity };
        if (hoursValue is not null and not WireNull)
        {
            backupHours = Saturate(WireConvert.ToInt(hoursValue));
            if (backupHours is < 1 or > 720)
            {
                throw new WireValueException("Backup interval must be between 1 and 720 hours.");
            }
        }

        var backupTime = ss.Get("configuration_backup_preferred_time") is WireString timeValue ? SuiteSettingsRules.NormalizeBackupPreferredTime(timeValue.Value) : null;
        normalized = normalized with { ConfigurationBackupEnabled = backupEnabled, ConfigurationBackupIntervalHours = backupHours, ConfigurationBackupPreferredTime = backupTime };
        var before = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
        var restored = SuiteSettingsRules.Apply(before, normalized);
        if (ss.Get("direct_play_devices") is WireString devices)
        {
            restored = restored with { DirectPlayDevices = devices.Value };
        }

        await _suiteSettings.UpdateAsync(uow, before, restored).ConfigureAwait(false);
    }

    private static async Task ApplySingletonAsync(UnitOfWork uow, string table, WireValue section)
    {
        var data = section as WireObject ?? throw new WireTypeException($"The backup's {table} section must be an object.");
        var columns = await ColumnsAsync(uow, table).ConfigureAwait(false);
        var kwargs = ToKwargs(columns, data);
        var pk = kwargs.GetValueOrDefault("id");
        var loaded = pk is null or WireNull ? null : await ReadTypedRowAsync(uow, table, columns, pk).ConfigureAwait(false);
        if (loaded is null)
        {
            await InsertAsync(uow, table, columns, kwargs).ConfigureAwait(false);
        }
        else
        {
            var sets = new List<string>();
            var parameters = new List<(string, object?)>();
            foreach (var (key, value) in kwargs)
            {
                var column = columns[key];
                if (!ValuesEqual(column.Kind, loaded[key], value))
                {
                    sets.Add($"\"{key}\"=${key}");
                    parameters.Add(($"${key}", Bind(column, value)));
                }
            }

            if (sets.Count > 0)
            {
                if (columns.ContainsKey("updated_at") && !parameters.Any(p => p.Item1 == "$updated_at"))
                {
                    sets.Add("updated_at=CURRENT_TIMESTAMP");
                }

                parameters.Add(("$__pk", loaded["id"] is WireValue id ? WireConvert.ToDatabase(id) : DBNull.Value));
                await uow.ExecuteAsync($"UPDATE {table} SET {string.Join(", ", sets)} WHERE id = $__pk", [.. parameters]).ConfigureAwait(false);
            }
        }
    }
}
