using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;

namespace Weir.Api.Endpoints;

/// <summary>Reading and updating the operator-editable processing automation settings.</summary>
public static class ProcessingOperatorSettingsEndpoints
{
    public static IEndpointRouteBuilder MapProcessingOperatorSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingOperatorSettingsEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/operator-settings", handlers.GetOperatorSettingsAsync);
        endpoints.MapV1("PUT", "/processing/operator-settings", handlers.PutOperatorSettingsAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingOperatorSettingsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingOperatorSettingsEndpointHandlers
{
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly ScanSettingsChanges _scanSettingsChanges;
    private readonly SuiteSettingsStore _suiteSettings;

    public ProcessingOperatorSettingsEndpointHandlers(
        OperatorSettingsStore operatorSettings, ScanSettingsChanges scanSettingsChanges, SuiteSettingsStore suiteSettings)
    {
        _operatorSettings = operatorSettings ?? throw new ArgumentNullException(nameof(operatorSettings));
        _scanSettingsChanges = scanSettingsChanges ?? throw new ArgumentNullException(nameof(scanSettingsChanges));
        _suiteSettings = suiteSettings ?? throw new ArgumentNullException(nameof(suiteSettings));
    }

    private static WireObject OperatorSettingsOut(ProcessingOperatorSettingsRecord row, string timezone) => new WireObject()
        .Set("max_concurrent_files", OperatorSettingsRules.ClampMaxConcurrentFiles(row.MaxConcurrentFiles))
        .Set("runner_capacity", OperatorSettingsRules.ClampRunnerCapacity(row.RunnerCapacity))
        .Set("runner_cost_sd", OperatorSettingsRules.ClampRunnerCost(row.RunnerCostSd))
        .Set("runner_cost_720p", OperatorSettingsRules.ClampRunnerCost(row.RunnerCost720P))
        .Set("runner_cost_1080p", OperatorSettingsRules.ClampRunnerCost(row.RunnerCost1080P))
        .Set("runner_cost_4k", OperatorSettingsRules.ClampRunnerCost(row.RunnerCost4K))
        .Set("runner_budget_enabled", row.RunnerBudgetEnabled)
        .Set("work_temp_stale_sweep_enabled", row.WorkTempStaleSweepEnabled)
        // Null: the interval the environment gives. Setup › Performance › Cleanup shows the one in force from /processing/maintenance.
        .Set("work_temp_stale_sweep_interval_seconds", row.WorkTempStaleSweepIntervalSeconds is { } sweepEvery ? WireValue.Of(sweepEvery) : WireValue.Null)
        // #652: Setup › Performance › Cleanup › Unclaimed hand-backs. Off until a person switches it on; a null interval is six hours.
        .Set("unclaimed_handback_cleanup_enabled", row.UnclaimedHandbackCleanupEnabled)
        .Set("unclaimed_handback_window_days", OperatorSettingsRules.ClampUnclaimedHandbackWindowDays(row.UnclaimedHandbackWindowDays))
        .Set("unclaimed_handback_cleanup_interval_seconds", row.UnclaimedHandbackCleanupIntervalSeconds is { } unclaimedEvery ? WireValue.Of(unclaimedEvery) : WireValue.Null)
        .Set("keep_failed_work_files", row.KeepFailedWorkFiles)
        .Set("file_log_retention_days", OperatorSettingsRules.ClampFileLogRetentionDays(row.FileLogRetentionDays))
        .Set("movie_schedule_enabled", row.MovieScheduleEnabled)
        .Set("tv_schedule_enabled", row.TvScheduleEnabled)
        .Set("schedule_timezone", timezone)
        .Set("updated_at", row.UpdatedAt.IsoFormat());

    public async Task<ApiResult> GetOperatorSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await _operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        var suite = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
        return ApiRoutes.Ok(OperatorSettingsOut(row, string.IsNullOrWhiteSpace(suite.AppTimezone) ? "UTC" : suite.AppTimezone.Trim()));
    }

    /// <summary>
    /// Retired Performance settings. The wait for a new file, the minimum size and the space to keep free belong to each
    /// workflow, an unknown resolution costs what a 1080p file does, each workflow's weekly schedule decides when it works
    /// (so Movies and TV have no hours window of their own), and nothing acts on verbose detection records or on the
    /// failed-download cleanup. An older client or backup may still send them, so they are accepted and never read.
    /// </summary>
    private static readonly string[] RetiredSettings =
    [
        "verbose_detection_logging", "min_file_age_seconds", "min_input_file_size_mb", "minimum_free_disk_space_mb",
        "runner_cost_undetermined", "failure_cleanup_enabled", "failure_cleanup_interval_seconds",
        "movie_schedule_hours_limited", "movie_schedule_days", "movie_schedule_start", "movie_schedule_end",
        "tv_schedule_hours_limited", "tv_schedule_days", "tv_schedule_start", "tv_schedule_end",
    ];

    public async Task<ApiResult> PutOperatorSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        model.AcceptAndIgnore(RetiredSettings);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var maxConcurrentFiles = model.OptionalInt("max_concurrent_files", ge: 1, le: OperatorSettingsRules.MaxFilesAtOnce);
        var runnerCapacity = model.OptionalInt("runner_capacity", ge: 1, le: 64);
        var runnerCostSd = model.OptionalInt("runner_cost_sd", ge: 0, le: 64);
        var runnerCost720P = model.OptionalInt("runner_cost_720p", ge: 0, le: 64);
        var runnerCost1080P = model.OptionalInt("runner_cost_1080p", ge: 0, le: 64);
        var runnerCost4K = model.OptionalInt("runner_cost_4k", ge: 0, le: 64);
        var runnerBudgetEnabled = model.OptionalBool("runner_budget_enabled");
        var workTempStaleSweepEnabled = model.OptionalBool("work_temp_stale_sweep_enabled");
        var workTempStaleSweepIntervalSeconds = model.OptionalInt(
            "work_temp_stale_sweep_interval_seconds", ge: OperatorSettingsRules.MinCleanupIntervalSeconds, le: OperatorSettingsRules.MaxCleanupIntervalSeconds);
        var unclaimedHandbackCleanupEnabled = model.OptionalBool("unclaimed_handback_cleanup_enabled");
        var unclaimedHandbackWindowDays = model.OptionalInt(
            "unclaimed_handback_window_days", ge: HandbackRules.MinUnclaimedWindowDays, le: HandbackRules.MaxUnclaimedWindowDays);
        var unclaimedHandbackCleanupIntervalSeconds = model.OptionalInt(
            "unclaimed_handback_cleanup_interval_seconds", ge: OperatorSettingsRules.MinCleanupIntervalSeconds, le: OperatorSettingsRules.MaxCleanupIntervalSeconds);
        var keepFailedWorkFiles = model.OptionalBool("keep_failed_work_files");
        var fileLogRetentionDays = model.OptionalInt("file_log_retention_days", ge: 0, le: 3650);
        var movieScheduleEnabled = model.OptionalBool("movie_schedule_enabled");
        var tvScheduleEnabled = model.OptionalBool("tv_schedule_enabled");
        model.Finish(ExtraFields.Forbid);

        var hasProcessField = maxConcurrentFiles is not null || runnerCapacity is not null || runnerCostSd is not null ||
                               runnerCost720P is not null || runnerCost1080P is not null || runnerCost4K is not null ||
                               runnerBudgetEnabled is not null || workTempStaleSweepEnabled is not null ||
                               workTempStaleSweepIntervalSeconds is not null ||
                               unclaimedHandbackCleanupEnabled is not null || unclaimedHandbackWindowDays is not null ||
                               unclaimedHandbackCleanupIntervalSeconds is not null ||
                               keepFailedWorkFiles is not null || fileLogRetentionDays is not null || RetiredSettings.Any(model.Has);
        if (!hasProcessField && movieScheduleEnabled is null && tvScheduleEnabled is null)
        {
            issues.Add(new ValidationIssue("value_error", ["body"], "Value error, No operator settings fields to update.", WireValue.Null));
        }

        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var before = await _operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        var after = before;
        if (maxConcurrentFiles is { } m)
        {
            after = after with { MaxConcurrentFiles = OperatorSettingsRules.ClampMaxConcurrentFiles(m) };
        }

        if (runnerBudgetEnabled is { } budgetOn)
        {
            after = after with { RunnerBudgetEnabled = budgetOn };
        }

        if (runnerCapacity is { } rc)
        {
            after = after with { RunnerCapacity = Math.Clamp(rc, 0, 64) };
        }

        if (runnerCostSd is { } v1)
        {
            after = after with { RunnerCostSd = Math.Clamp(v1, 0, 64) };
        }

        if (runnerCost720P is { } v2)
        {
            after = after with { RunnerCost720P = Math.Clamp(v2, 0, 64) };
        }

        if (runnerCost1080P is { } v3)
        {
            after = after with { RunnerCost1080P = Math.Clamp(v3, 0, 64) };
        }

        if (runnerCost4K is { } v4)
        {
            after = after with { RunnerCost4K = Math.Clamp(v4, 0, 64) };
        }

        if (fileLogRetentionDays is { } fl)
        {
            after = after with { FileLogRetentionDays = OperatorSettingsRules.ClampFileLogRetentionDays(fl) };
        }

        if (workTempStaleSweepEnabled is { } wt)
        {
            after = after with { WorkTempStaleSweepEnabled = wt };
        }

        if (workTempStaleSweepIntervalSeconds is { } sweepEvery)
        {
            after = after with { WorkTempStaleSweepIntervalSeconds = sweepEvery };
        }

        if (unclaimedHandbackCleanupEnabled is { } unclaimedOn)
        {
            after = after with { UnclaimedHandbackCleanupEnabled = unclaimedOn };
        }

        if (unclaimedHandbackWindowDays is { } unclaimedDays)
        {
            after = after with { UnclaimedHandbackWindowDays = OperatorSettingsRules.ClampUnclaimedHandbackWindowDays(unclaimedDays) };
        }

        if (unclaimedHandbackCleanupIntervalSeconds is { } unclaimedEvery)
        {
            after = after with { UnclaimedHandbackCleanupIntervalSeconds = unclaimedEvery };
        }

        if (keepFailedWorkFiles is { } kf)
        {
            after = after with { KeepFailedWorkFiles = kf };
        }

        if (movieScheduleEnabled is { } movieOn)
        {
            after = after with { MovieScheduleEnabled = movieOn };
        }

        if (tvScheduleEnabled is { } tvOn)
        {
            after = after with { TvScheduleEnabled = tvOn };
        }

        await _operatorSettings.UpdateAsync(uow, before, after).ConfigureAwait(false);
        var updated = await _operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        var suite = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        // The periodic-scan switches are among these settings; the scheduler reads them again on its next tick.
        _scanSettingsChanges.Record();
        return ApiRoutes.Ok(OperatorSettingsOut(updated, string.IsNullOrWhiteSpace(suite.AppTimezone) ? "UTC" : suite.AppTimezone.Trim()));
    }
}
