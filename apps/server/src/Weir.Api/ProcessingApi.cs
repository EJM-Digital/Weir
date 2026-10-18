using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Api.Endpoints;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.DirectPlay;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Scheduling;

namespace Weir.Api;

/// <summary>
/// DI registration and route wiring for the Processing libraries, file state, files/inspection/maintenance/
/// overview/hardware/Direct Play APIs (#522). One extension per side, additive only: it never touches
/// <see cref="Weir.Api.WeirApi"/>'s own registrations, so other areas can add their own <c>AddWeirXxx</c>
/// alongside this without conflict.
/// </summary>
public static class ProcessingApi
{
    /// <summary>Services the Processing endpoints need beyond those <c>AddWeirApi</c> and <c>AddWeirJobs</c>
    /// already register.</summary>
    public static IServiceCollection AddWeirProcessingApis(this IServiceCollection services)
    {
        services.AddWeirMediaTools();
        // Caps the #502 "Try on a file" preview at one run at a time (see RulesPreviewGate's own docs).
        services.TryAddSingleton<RulesPreviewGate>();
        // The folder-chain check composes ManagerSetupCheck (already registered by AddWeirMediaManagerServices)
        // with Weir's own watched/work/output folder rules.
        services.TryAddSingleton<FolderChainAnswers>();
        services.TryAddSingleton<LibraryFolderChainCheck>();
        // Checked again on its own while a browser is watching, so a folder going missing or coming back is pushed, not asked for.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPeriodicTask, FolderChecksTask>());
        // Looks at Weir's own folders whether or not a browser is open, because the tray's dot turns amber for one it cannot reach.
        services.TryAddSingleton<FolderReachability>();
        services.AddHostedService(provider => provider.GetRequiredService<FolderReachability>());
        // First-run setup offers libraries from what is connected, and checks the ones a person confirms.
        services.TryAddSingleton<LibrarySuggestions>();
        services.TryAddSingleton<ProposedLibraryCheck>();

        // Processing's own stores (#745 part 5): stateless SQL access over the caller's UnitOfWork, so a
        // singleton is as cheap as a static class was and lets endpoints and handlers take them by constructor.
        // LibraryStore and FileStateStore are registered by AddWeirPlatform: both are reached from Jobs,
        // LibraryMode and MediaManagers as well as Processing.
        services.TryAddSingleton<FileLogStore>();
        services.TryAddSingleton<HoldDiagnosticStore>();
        services.TryAddSingleton<JobsInspectionStore>();
        services.TryAddSingleton<MaintenanceStore>();
        services.TryAddSingleton<OperatorSettingsStore>();
        services.TryAddSingleton<OverviewStatsStore>();
        // The Direct Play device list lives in SuiteSettingsStore; wrapping it here (#745 part 5) means both
        // callers take one dependency instead of each holding SuiteSettingsStore only to forward it.
        services.TryAddSingleton<DirectPlayService>();

        // Endpoint handler classes (#745 part 5): each endpoint file's real dependencies, constructor-injected
        // and resolved once when routes are mapped, rather than looked up per call through the request.
        services.AddSingleton<ArtworkEndpointHandlers>();
        services.AddSingleton<LibraryModeEndpointHandlers>();
        services.AddSingleton<LibraryModeFilesEndpointHandlers>();
        services.AddSingleton<LibraryModeOverviewEndpointHandlers>();
        services.AddSingleton<LibraryModeRedownloadsEndpointHandlers>();
        services.AddSingleton<LibraryModeScheduleEndpointHandlers>();
        services.AddSingleton<ProcessingDirectPlayEndpointHandlers>();
        services.AddSingleton<ProcessingFileLogEndpointHandlers>();
        services.AddSingleton<ProcessingFileTracksEndpointHandlers>();
        services.AddSingleton<ProcessingFilesEndpointHandlers>();
        services.AddSingleton<ProcessingJobsEndpointHandlers>();
        services.AddSingleton<ProcessingKeptFilesEndpointHandlers>();
        services.AddSingleton<ProcessingLibraryEndpointHandlers>();
        services.AddSingleton<ProcessingLibraryCleansEndpointHandlers>();
        services.AddSingleton<ProcessingLibraryDiscoveryEndpointHandlers>();
        services.AddSingleton<ProcessingLibrarySetupEndpointHandlers>();
        services.AddSingleton<ProcessingMetadataProviderEndpointHandlers>();
        services.AddSingleton<ProcessingOperatorSettingsEndpointHandlers>();
        services.AddSingleton<ProcessingOverviewMaintenanceEndpointHandlers>();
        services.AddSingleton<ProcessingRejectedFilesEndpointHandlers>();
        services.AddSingleton<ProcessingRemuxPassEndpointHandlers>();
        services.AddSingleton<ProcessingRuleSetsEndpointHandlers>();
        services.AddSingleton<ProcessingRulesPreviewEndpointHandlers>();
        services.AddSingleton<ProcessingRuntimeEndpointHandlers>();
        services.AddSingleton<ProcessingWatchedFolderScanDispatchEndpointHandlers>();

        // The only "processing-file-log-retention" task (#546): it uses the same UnitOfWork/
        // OperatorSettingsStore/FileLogStore plumbing as the rest of Processing.
        services.AddSingleton<FileLogRetentionTask>();
        services.AddSingleton<IPeriodicTask>(sp => sp.GetRequiredService<FileLogRetentionTask>());

        // Watched-folder scan dispatch (#522 part 5): the job handler that runs a scan, and the periodic
        // scheduler that enqueues one per library. Additive: the job handler registry (AddWeirJobs) simply
        // gains one more kind it can claim, and the periodic task joins the others already registered.
        services.AddSingleton<ScanWakeups>();
        services.AddSingleton<ProcessingWatchedFolderScanDispatchJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<ProcessingWatchedFolderScanDispatchJobHandler>());
        services.AddSingleton<IPeriodicTask, ProcessingWatchedFolderScanDispatchScheduleTask>();
        // Forgets the rows of files that left a watched folder even when that library's scan is off.
        services.AddSingleton<IPeriodicTask, VanishedFileSweepTask>();

        // Watched-folder filesystem watcher (#552): FileSystemWatcher per enabled/watched library, feeding
        // the same scan-dispatch enqueue above. Runs independently of the periodic scheduler — see
        // ProcessingWatchedFolderWatcherService's own docs for what it does.
        services.AddHostedService<ProcessingWatchedFolderWatcherService>();
        services.AddHostedService<WorkFolderPlacementCheck>();
        return services;
    }

    /// <summary>Maps every Processing route (see the docs on each endpoint class for what it covers).</summary>
    public static IEndpointRouteBuilder MapWeirProcessingApis(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapProcessingLibraryEndpoints();
        endpoints.MapProcessingLibraryDiscoveryEndpoints();
        endpoints.MapProcessingLibrarySetupEndpoints();
        endpoints.MapProcessingRuleSetsEndpoints();
        endpoints.MapProcessingFilesEndpoints();
        endpoints.MapArtworkEndpoints();
        endpoints.MapProcessingFileLogEndpoints();
        endpoints.MapProcessingFileTracksEndpoints();
        endpoints.MapProcessingLibraryCleansEndpoints();
        endpoints.MapProcessingDirectPlayEndpoints();
        endpoints.MapProcessingJobsEndpoints();
        endpoints.MapProcessingKeptFilesEndpoints();
        endpoints.MapProcessingRejectedFilesEndpoints();
        endpoints.MapProcessingRemuxPassEndpoints();
        endpoints.MapProcessingRulesPreviewEndpoints();
        endpoints.MapProcessingOverviewMaintenanceEndpoints();
        endpoints.MapProcessingSettingsEndpoints();
        endpoints.MapProcessingWatchedFolderScanDispatchEndpoints();
        endpoints.MapLibraryModeEndpoints();
        endpoints.MapLibraryModeOverviewEndpoints();
        endpoints.MapLibraryModeFilesEndpoints();
        endpoints.MapLibraryModeScheduleEndpoints();
        endpoints.MapLibraryModeRedownloadsEndpoints();
        return endpoints;
    }
}
