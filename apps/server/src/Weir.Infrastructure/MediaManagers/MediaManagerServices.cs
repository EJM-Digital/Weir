using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.MediaManagers;
using Weir.Core.Security;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Registers the media manager area (#520): connections, ports, intake, the hand-off ledger and completion reports.</summary>
public static class MediaManagerServices
{
    public static IServiceCollection AddWeirMediaManagers(this IServiceCollection services, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWeirPlatform(options);
        services.TryAddSingleton(sp => new CredentialCipher(
            options.CredentialsSecret, options.SessionSecret, options.PreviousCredentialsSecrets, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IManagerHttpHandlerFactory, SocketsManagerHttpHandlerFactory>();
        services.TryAddSingleton<IMediaManagerPorts, HttpMediaManagerPorts>();
        // Media manager stores (#745 part 5): stateless SQL access over the caller's UnitOfWork, so a
        // singleton is as cheap as a static class was and lets callers take them by constructor.
        services.TryAddSingleton<MediaManagerConnectionStore>();
        services.TryAddSingleton<ArrLibraryOperatorSettingsStore>();
        services.TryAddSingleton<HandbackStore>();
        services.TryAddSingleton<HandoffTargetStore>();
        services.TryAddSingleton<MediaManagerConnectionService>();
        // What still depends on a connection, which decides whether it can be removed and lets its workflows go first.
        services.TryAddSingleton<MediaManagerConnectionDependents>();
        // Every minute, each manager's connection test, so Weir knows within a minute when one goes quiet.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPeriodicTask, ManagerHeartbeatTask>());
        services.TryAddSingleton<LibraryDiscoveryService>();
        // Deluno reports its own folders, so the workflows linked to it are set up from it and kept in step every few minutes.
        services.TryAddSingleton<ManagerWorkflowSync>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPeriodicTask, ManagerWorkflowSyncTask>());
        services.TryAddSingleton<IFolderProbe, FilesystemFolderProbe>();
        services.TryAddSingleton<ManagerSetupCheck>();
        services.TryAddSingleton<HandoffLedgerStore>();
        services.AddWeirJobStore();
        services.AddWeirArtwork();
        services.TryAddSingleton<MediaManagerIntake>();
        services.TryAddSingleton<HandoffCompletionReporter>();
        // Cancelling one queued job from the jobs list in System › Logs (#745 part 5): the ledger, the report and the file
        // state it touches, constructor-injected instead of passed in on every call.
        services.TryAddSingleton<PendingJobCancellation>();
        // #652: what a manager said about a file Weir handed back, and the one rule that releases Weir's copy.
        services.TryAddSingleton<HandbackOutcomes>();
        // The optional downloaded-scan hand-back for a Sonarr/Radarr connection outside Weir's own hand-off flow.
        services.TryAddSingleton<DownloadedScanNotifier>();
        // A manager that was busy with another scan is asked again later, as a queued job rather than a worker that waits.
        services.TryAddSingleton<DownloadedScanRetryHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, DownloadedScanRetryHandler>(sp => sp.GetRequiredService<DownloadedScanRetryHandler>()));
        services.TryAddSingleton<ILibraryFileChangeNotifier, LibraryFileChangeNotifier>();

        // #509: asking a manager to redownload a title. What a clean removed, and which titles wait for a
        // redownload, belong to library mode and are registered there, once.
        services.TryAddSingleton<IManagerRedownload, ArrManagerRedownload>();
        return services;
    }
}
