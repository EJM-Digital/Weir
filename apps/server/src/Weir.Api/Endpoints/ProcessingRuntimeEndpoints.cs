using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;

namespace Weir.Api.Endpoints;

/// <summary>Read-only runtime probes: what is running right now, the worker/runtime snapshot, and hardware-acceleration detection.</summary>
public static class ProcessingRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapProcessingRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingRuntimeEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/runtime-settings", handlers.GetRuntimeSettingsAsync);
        endpoints.MapV1("GET", "/processing/files-at-once", handlers.GetFilesAtOnceAsync);
        endpoints.MapV1("GET", "/processing/hardware", handlers.GetHardwareAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingRuntimeEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingRuntimeEndpointHandlers
{
    private readonly ProcessingJobStore _jobs;
    private readonly TimeProvider _time;
    private readonly IMediaToolResolver _resolver;
    private readonly MediaTools _mediaTools;

    public ProcessingRuntimeEndpointHandlers(ProcessingJobStore jobs, TimeProvider time, IMediaToolResolver resolver, MediaTools mediaTools)
    {
        _jobs = jobs;
        _time = time;
        _resolver = resolver;
        _mediaTools = mediaTools;
    }

    /// <summary>What is running, what is waiting and which limit the waiting files are waiting on (#633).</summary>
    public async Task<ApiResult> GetFilesAtOnceAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var slots = request.Options.ProcessingWorkerCount;
        // A read, so never the queue's write transaction (#636): under several remuxes that queued behind the workers
        // until it timed out with "database is locked".
        var readout = await _jobs.ReadAsync(
            (connection, transaction) => WorkAdmissionReader.ReadFilesAtOnce(connection, transaction, now, slots)).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("files_at_once", readout.FilesAtOnce)
            .Set("worker_slots", readout.WorkerSlots)
            .Set("effective_files_at_once", readout.Effective)
            .Set("running", readout.Running)
            .Set("waiting", readout.Waiting)
            .Set("waiting_for", readout.WaitingFor)
            .Set("message", readout.Message)
            .Set("slots_note", readout.SlotsNote));
    }

    public async Task<ApiResult> GetRuntimeSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var settings = RuntimeVisibility.From(request.Options);
        return ApiRoutes.Ok(new WireObject()
            .Set("in_process_processing_worker_count", settings.InProcessProcessingWorkerCount)
            .Set("in_process_workers_disabled", settings.InProcessWorkersDisabled)
            .Set("in_process_workers_enabled", settings.InProcessWorkersEnabled)
            .Set("worker_mode_summary", settings.WorkerModeSummary)
            .Set("sqlite_throughput_note", settings.SqliteThroughputNote)
            .Set("configuration_note", settings.ConfigurationNote)
            .Set("visibility_note", settings.VisibilityNote)
            .Set("processing_media_extensions", new WireArray(settings.ProcessingMediaExtensions.Select(e => (WireValue)WireValue.Of(e))))
            .Set("processing_watched_folder_remux_scan_dispatch_periodic_enqueue_remux_jobs", settings.ProcessingWatchedFolderRemuxScanDispatchPeriodicEnqueueRemuxJobs)
            .Set("processing_probe_size_mb", settings.ProcessingProbeSizeMb)
            .Set("processing_analyze_duration_seconds", settings.ProcessingAnalyzeDurationSeconds)
            .Set("processing_movie_output_cleanup_min_age_seconds", settings.ProcessingMovieOutputCleanupMinAgeSeconds)
            .Set("movie_output_cleanup_configuration_note", settings.MovieOutputCleanupConfigurationNote)
            .Set("processing_tv_output_cleanup_min_age_seconds", settings.ProcessingTvOutputCleanupMinAgeSeconds)
            .Set("tv_output_cleanup_configuration_note", settings.TvOutputCleanupConfigurationNote)
            .Set("watched_folder_scan_periodic_configuration_note", settings.WatchedFolderScanPeriodicConfigurationNote)
            .Set("processing_work_temp_stale_sweep_movie_schedule_enabled", settings.ProcessingWorkTempStaleSweepMovieScheduleEnabled)
            .Set("processing_work_temp_stale_sweep_movie_schedule_interval_seconds", settings.ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds)
            .Set("processing_work_temp_stale_sweep_tv_schedule_enabled", settings.ProcessingWorkTempStaleSweepTvScheduleEnabled)
            .Set("processing_work_temp_stale_sweep_tv_schedule_interval_seconds", settings.ProcessingWorkTempStaleSweepTvScheduleIntervalSeconds)
            .Set("processing_work_temp_stale_sweep_min_stale_age_seconds", settings.ProcessingWorkTempStaleSweepMinStaleAgeSeconds)
            .Set("work_temp_stale_sweep_periodic_configuration_note", settings.WorkTempStaleSweepPeriodicConfigurationNote));
    }

    public async Task<ApiResult> GetHardwareAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        string ffmpeg;
        try
        {
            (_, ffmpeg) = _resolver.Resolve();
        }
        catch (MediaToolException exception)
        {
            return ApiRoutes.Ok(new WireObject()
                .Set("detected", false)
                .Set("available_methods", new WireArray([]))
                .Set("vendors", new WireArray([]))
                .Set("selectable_vendors", new WireArray(HardwareAcceleration.VendorMethods.Select(v => v.Key).Order(StringComparer.Ordinal).Select(k => (WireValue)WireValue.Of(k))))
                .Set("strictness_levels", new WireArray(FfmpegStrictnessLevels.All.Select(l => (WireValue)WireValue.Of(l))))
                .Set("detail", $"Weir could not find ffmpeg, so it cannot report acceleration methods. {exception.Message}"));
        }

        var report = await _mediaTools.DetectAccelerationAsync(ffmpeg, request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("detected", report.Detected)
            .Set("available_methods", new WireArray(report.AvailableMethods.Select(m => (WireValue)WireValue.Of(m))))
            .Set("vendors", new WireArray(report.Vendors.Select(v => (WireValue)WireValue.Of(v))))
            .Set("selectable_vendors", new WireArray(HardwareAcceleration.VendorMethods.Select(v => v.Key).Order(StringComparer.Ordinal).Select(k => (WireValue)WireValue.Of(k))))
            .Set("strictness_levels", new WireArray(FfmpegStrictnessLevels.All.Select(l => (WireValue)WireValue.Of(l))))
            .Set("detail", report.Detail));
    }
}
