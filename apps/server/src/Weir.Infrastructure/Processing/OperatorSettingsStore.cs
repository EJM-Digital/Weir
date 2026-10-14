using Microsoft.Data.Sqlite;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>The singleton <c>operator_settings</c> row.</summary>
/// <remarks>
/// <c>verbose_detection_logging</c>, <c>runner_cost_undetermined</c>, <c>minimum_free_disk_space_mb</c>, <c>failure_cleanup_enabled</c>
/// and <c>failure_cleanup_interval_seconds</c> are columns that nothing reads or writes: nothing acts on the first and
/// the last two, a file of unknown resolution costs what 1080p does, and free space to keep is each workflow's own. Dropping them
/// would buy nothing and cost a migration, so they keep their defaults on a new row; a configuration backup carries them and
/// restores them like any other column, which is how an older backup still restores, and how its free-space value reaches its
/// workflows.
/// </remarks>
public sealed class OperatorSettingsStore
{
    private const string Columns =
        "max_concurrent_files, runner_capacity, runner_cost_sd, runner_cost_720p, runner_cost_1080p, runner_cost_4k, " +
        "work_temp_stale_sweep_enabled, keep_failed_work_files, file_log_retention_days, " +
        "movie_schedule_enabled, tv_schedule_enabled, updated_at, runner_budget_enabled, work_temp_stale_sweep_interval_seconds, " +
        "unclaimed_handback_cleanup_enabled, unclaimed_handback_window_days, " +
        "unclaimed_handback_cleanup_interval_seconds";

    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once an update commits, that these settings changed, so every open screen reads them again.</param>
    public OperatorSettingsStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public Task<ProcessingOperatorSettingsRecord?> GetAsync(UnitOfWork uow) =>
        uow.QuerySingleAsync($"SELECT {Columns} FROM operator_settings WHERE id = 1", Read);

    /// <summary>The settings row, created with its defaults when it does not exist yet.</summary>
    public async Task<ProcessingOperatorSettingsRecord> EnsureAsync(UnitOfWork uow)
    {
        var row = await GetAsync(uow).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        await uow.ExecuteAsync(
            "INSERT INTO operator_settings (id, max_concurrent_files, runner_capacity, runner_cost_sd, runner_cost_720p, " +
            "runner_cost_1080p, runner_cost_4k, work_temp_stale_sweep_enabled, " +
            "keep_failed_work_files, file_log_retention_days, movie_schedule_enabled, tv_schedule_enabled) " +
            "VALUES (1, 1, 4, 0, 0, 1, 1, 1, 0, 90, 1, 1)").ConfigureAwait(false);
        return await GetAsync(uow).ConfigureAwait(false) ?? throw new InvalidOperationException("operator_settings row was not created.");
    }

    public async Task UpdateAsync(UnitOfWork uow, ProcessingOperatorSettingsRecord before, ProcessingOperatorSettingsRecord after)
    {
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

        Compare("max_concurrent_files", before.MaxConcurrentFiles, after.MaxConcurrentFiles, v => v);
        Compare("runner_capacity", before.RunnerCapacity, after.RunnerCapacity, v => v);
        Compare("runner_cost_sd", before.RunnerCostSd, after.RunnerCostSd, v => v);
        Compare("runner_cost_720p", before.RunnerCost720P, after.RunnerCost720P, v => v);
        Compare("runner_cost_1080p", before.RunnerCost1080P, after.RunnerCost1080P, v => v);
        Compare("runner_cost_4k", before.RunnerCost4K, after.RunnerCost4K, v => v);
        Compare("runner_budget_enabled", before.RunnerBudgetEnabled, after.RunnerBudgetEnabled, v => v ? 1 : 0);
        Compare("work_temp_stale_sweep_enabled", before.WorkTempStaleSweepEnabled, after.WorkTempStaleSweepEnabled, v => v ? 1 : 0);
        Compare("work_temp_stale_sweep_interval_seconds", before.WorkTempStaleSweepIntervalSeconds, after.WorkTempStaleSweepIntervalSeconds, v => v);
        Compare("unclaimed_handback_cleanup_enabled", before.UnclaimedHandbackCleanupEnabled, after.UnclaimedHandbackCleanupEnabled, v => v ? 1 : 0);
        Compare("unclaimed_handback_window_days", before.UnclaimedHandbackWindowDays, after.UnclaimedHandbackWindowDays, v => v);
        Compare("unclaimed_handback_cleanup_interval_seconds", before.UnclaimedHandbackCleanupIntervalSeconds, after.UnclaimedHandbackCleanupIntervalSeconds, v => v);
        Compare("keep_failed_work_files", before.KeepFailedWorkFiles, after.KeepFailedWorkFiles, v => v ? 1 : 0);
        Compare("file_log_retention_days", before.FileLogRetentionDays, after.FileLogRetentionDays, v => v);
        Compare("movie_schedule_enabled", before.MovieScheduleEnabled, after.MovieScheduleEnabled, v => v ? 1 : 0);
        Compare("tv_schedule_enabled", before.TvScheduleEnabled, after.TvScheduleEnabled, v => v ? 1 : 0);
        if (sets.Count == 0)
        {
            return;
        }

        sets.Add("updated_at=CURRENT_TIMESTAMP");
        await uow.ExecuteAsync($"UPDATE operator_settings SET {string.Join(", ", sets)} WHERE id = 1", [.. parameters]).ConfigureAwait(false);
        _changes?.PublishOnCommit(uow, DataTopics.Settings);
    }

    private static ProcessingOperatorSettingsRecord Read(SqliteDataReader reader) => new()
    {
        MaxConcurrentFiles = SqliteValues.GetInt64(reader, 0),
        RunnerCapacity = SqliteValues.GetInt64(reader, 1),
        RunnerCostSd = SqliteValues.GetInt64(reader, 2),
        RunnerCost720P = SqliteValues.GetInt64(reader, 3),
        RunnerCost1080P = SqliteValues.GetInt64(reader, 4),
        RunnerCost4K = SqliteValues.GetInt64(reader, 5),
        WorkTempStaleSweepEnabled = SqliteValues.GetBool(reader, 6),
        KeepFailedWorkFiles = SqliteValues.GetBool(reader, 7),
        FileLogRetentionDays = SqliteValues.GetInt64(reader, 8),
        MovieScheduleEnabled = SqliteValues.GetBool(reader, 9),
        TvScheduleEnabled = SqliteValues.GetBool(reader, 10),
        UpdatedAt = SqliteValues.GetDateTime(reader, 11),
        RunnerBudgetEnabled = SqliteValues.GetBool(reader, 12),
        WorkTempStaleSweepIntervalSeconds = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        UnclaimedHandbackCleanupEnabled = SqliteValues.GetBool(reader, 14),
        UnclaimedHandbackWindowDays = SqliteValues.GetInt64(reader, 15),
        UnclaimedHandbackCleanupIntervalSeconds = reader.IsDBNull(16) ? null : reader.GetInt64(16),
    };
}
