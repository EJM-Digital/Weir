using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Library;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Library;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// Registers library mode (#505): the #506 safe swap (see <c>docs/archive/server-port-notes.md</c>, "Library mode: safe
/// swap"), its scan and clean job handlers, and the real
/// notify seam (#507, <c>Weir.Infrastructure.MediaManagers.LibraryFileChangeNotifier</c>, registered by
/// <c>AddWeirMediaManagers</c>).
/// </summary>
public static class LibraryModeServices
{
    public static IServiceCollection AddWeirLibraryMode(this IServiceCollection services, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWeirMediaManagers(options);
        services.AddWeirMediaTools();

        // Library mode's own stores (#745 part 5): stateless SQL access over the caller's UnitOfWork, so a
        // singleton is as cheap as a static class was and lets endpoints and handlers take them by constructor.
        services.TryAddSingleton<LibraryCleanHistoryStore>();
        services.TryAddSingleton<LibraryFileMarksStore>();
        services.TryAddSingleton<LibraryScanProgress>();
        services.TryAddSingleton<LibraryScanStore>();
        services.TryAddSingleton<LibrarySettingsStore>();
        services.TryAddSingleton<LibraryViewStore>();

        // The #506 safe swap: real files, the library_swaps journal, the #500 output check.
        services.TryAddSingleton<ISwapFileSystem>(sp => new PhysicalSwapFileSystem(sp.GetService<IFileSystem>()));
        services.TryAddSingleton(sp => new ProcessingJobSwapJournal(sp.GetRequiredService<SqliteDatabase>()));
        services.TryAddSingleton<ISwapJournal>(sp => sp.GetRequiredService<ProcessingJobSwapJournal>());
        services.TryAddSingleton<ISwapOutputValidator, RemuxOutputSwapValidator>();
        if (options.LibrarySwapHoldAt is { } holdAt)
        {
            services.AddSingleton<ISwapProgress>(HeldSwapProgress.FromSetting(holdAt, options.WeirHome));
        }

        services.TryAddSingleton<SafeSwap>();
        services.TryAddSingleton<SwapRecoverySweep>();

        // #508 preflight: hardlink (pure filesystem) and re-download-risk (arr custom-format prediction) checks.
        services.TryAddSingleton<IHardlinkInspector>(_ => PhysicalHardlinkInspector.Instance);
        services.TryAddSingleton<IRedownloadRiskGateway, ArrRedownloadRiskGateway>();
        services.TryAddSingleton<RedownloadRiskChecker>();

        // #509: what a clean removed for good, and asking a manager to redownload a title that is missing it.
        // The only registrations of these two: the removed_tracks table keeps removed-track history across restarts,
        // and the redownload tracker stays in memory (see its remarks). AddWeirMediaManagers registers the redownload
        // call itself.
        services.TryAddSingleton<IRemovedTrackStore, FileLogRemovedTrackStore>();
        services.TryAddSingleton<IRedownloadTracker, InMemoryRedownloadTracker>();

        // #507's notify seam: AddWeirMediaManagers (above) registers the real LibraryFileChangeNotifier.
        services.TryAddSingleton<LibraryScanHandler>();
        services.TryAddSingleton<LibraryCleanHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, LibraryScanHandler>(sp => sp.GetRequiredService<LibraryScanHandler>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, LibraryCleanHandler>(sp => sp.GetRequiredService<LibraryCleanHandler>()));

        // "Scheduled scan and clean": the timer that queues each library's daily scheduled scan.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Scheduling.IPeriodicTask, LibraryModeScheduleTask>());
        return services;
    }
}
