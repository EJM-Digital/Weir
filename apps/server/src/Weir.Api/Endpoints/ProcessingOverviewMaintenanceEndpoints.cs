using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>The custody-screen overview stats and the maintenance job families.</summary>
public static class ProcessingOverviewMaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapProcessingOverviewMaintenanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingOverviewMaintenanceEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/overview-stats", handlers.GetOverviewStatsAsync);
        endpoints.MapV1("GET", "/processing/maintenance", handlers.GetMaintenanceAsync);
        endpoints.MapV1("POST", "/processing/maintenance/run", handlers.PostMaintenanceRunAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingOverviewMaintenanceEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingOverviewMaintenanceEndpointHandlers
{
    private readonly OverviewStatsStore _overviewStats;
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly MaintenanceStore _maintenance;
    private readonly PeriodicEnqueueClock _clock;
    private readonly ProcessingJobStore _jobs;

    public ProcessingOverviewMaintenanceEndpointHandlers(
        OverviewStatsStore overviewStats, OperatorSettingsStore operatorSettings, MaintenanceStore maintenance, PeriodicEnqueueClock clock, ProcessingJobStore jobs)
    {
        _overviewStats = overviewStats ?? throw new ArgumentNullException(nameof(overviewStats));
        _operatorSettings = operatorSettings ?? throw new ArgumentNullException(nameof(operatorSettings));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    public async Task<ApiResult> GetOverviewStatsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var windowDays = request.Query("window_days") is { } raw && FieldRules.TryInt(new WireString(raw), ["query", "window_days"], 1, 3650, issues, out var parsed) ? (int)parsed : 30;
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var stats = await _overviewStats.BuildAsync(uow, windowDays, request.Time).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("window_days", stats.WindowDays)
            .Set("files_processed", stats.FilesProcessed)
            .Set("files_failed", stats.FilesFailed)
            .Set("success_rate_percent", stats.SuccessRatePercent)
            .Set("output_written_count", stats.OutputWrittenCount)
            .Set("already_optimized_count", stats.AlreadyOptimizedCount)
            .Set("net_space_saved_bytes", stats.NetSpaceSavedBytes)
            .Set("net_space_saved_percent", stats.NetSpaceSavedPercent));
    }

    /// <summary>
    /// One family, with how often it runs and when it next does (Setup › Performance › Cleanup). The next run comes from the family's
    /// own timer, so it is null while the family is switched off; the interval is the one in force either way.
    /// </summary>
    private static WireObject FamilyOut(MaintenanceFamilyState state, TimeSpan interval, DateTimeOffset? nextRunAt) => new WireObject()
        .Set("family", state.Family)
        .Set("enabled", state.Enabled)
        .Set("description", state.Description)
        .Set("pending", state.Pending)
        .Set("running", state.Running)
        .Set("last_completed_at", state.LastCompletedAt?.ToWireText())
        .Set("last_failed_at", state.LastFailedAt?.ToWireText())
        .Set("last_error", state.LastError)
        .Set("interval_seconds", (long)interval.TotalSeconds)
        .Set("next_run_at", nextRunAt is { } next ? Timestamp.FromDateTimeOffset(next).ToWireText() : null);

    public async Task<ApiResult> GetMaintenanceAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var operatorRow = await _operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        var sweep = await _maintenance.StateForAsync(uow, "work_temp_stale_sweep", operatorRow.WorkTempStaleSweepEnabled).ConfigureAwait(false);
        var unclaimed = await _maintenance.StateForAsync(uow, "unclaimed_handbacks", operatorRow.UnclaimedHandbackCleanupEnabled).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);

        var options = request.Options;
        WireObject Out(MaintenanceFamilyState state, long? saved, int environment)
        {
            var timer = _clock.NextRunFor(_maintenance.JobKindsFor(state.Family));
            var interval = timer?.Interval ?? TimeSpan.FromSeconds(saved is > 0 ? saved.Value : environment);
            return FamilyOut(state, interval, state.Enabled ? timer?.NextRunAt : null);
        }

        return ApiRoutes.Ok(new WireObject().Set("families", new WireArray(
        [
            Out(sweep, operatorRow.WorkTempStaleSweepIntervalSeconds, options.ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds),
            Out(unclaimed, operatorRow.UnclaimedHandbackCleanupIntervalSeconds, HandbackRules.DefaultUnclaimedIntervalSeconds)
                .Set("window_days", OperatorSettingsRules.ClampUnclaimedHandbackWindowDays(operatorRow.UnclaimedHandbackWindowDays)),
        ])));
    }

    public async Task<ApiResult> PostMaintenanceRunAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var family = model.Literal("family", _maintenance.Families);
        var mediaScope = model.Literal("media_scope", ["movie", "tv"], defaultValue: "movie");
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var scopeWord = mediaScope == "tv" ? "TV" : "Movies";
        if (family == "work_temp_stale_sweep")
        {
            await _maintenance.EnqueueWorkTempStaleSweepAsync(_jobs, mediaScope, "manual").ConfigureAwait(false);
            return ApiRoutes.Ok(new WireObject().Set("queued", true).Set("detail", $"Queued a work file sweep for {scopeWord}. It runs as soon as a worker is free."));
        }

        await _maintenance.EnqueueUnclaimedHandbackCleanupAsync(_jobs, mediaScope, "manual").ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("queued", true)
            .Set("detail", $"Queued the unclaimed hand-back cleanup for {scopeWord}. It runs as soon as a worker is free."));
    }
}
