using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Configuration;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Workers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure;

/// <summary>The singletons every area shares, registered once whichever area adds them first.</summary>
public static class WeirPlatformServices
{
    /// <summary>Options, the clock, the database, worker heartbeats, time zones and the Activity writer.</summary>
    public static IServiceCollection AddWeirPlatform(this IServiceCollection services, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFileSystem>(PhysicalFileSystem.Instance);
        services.TryAddSingleton(MachineIdentity.Current());
        services.TryAddSingleton(sp => new SqliteDatabase(
            options.DbPath,
            logger: sp.GetService<ILogger<SqliteDatabase>>(),
            busyTimeoutMilliseconds: options.SqliteBusyTimeoutSeconds * 1000));
        services.TryAddSingleton<WorkerHeartbeats>();
        services.TryAddSingleton<WatcherStateStore>();
        services.TryAddSingleton<ScanSettingsChanges>();
        services.TryAddSingleton<ITimeZoneResolver, IanaTimeZoneResolver>();
        services.TryAddSingleton<IActivityWriter, SqliteActivityWriter>();
        services.TryAddSingleton(sp => ActivityNotifications.For(sp.GetRequiredService<SqliteDatabase>()));
        // One process-wide live-progress store, shared by every running pass and every open stream (#750).
        services.TryAddSingleton<LiveProgressStore>();
        // What is happening on each media manager and download client connection, for the live Connections views.
        services.TryAddSingleton<ConnectionUsageLedger>();
        services.TryAddSingleton<ConnectionActivityHub>();
        services.TryAddSingleton<ActivityStreamClients>();
        // What the live stream announces: which data changed, and which run of the server this is.
        services.TryAddSingleton<DataChangePublisher>();
        services.TryAddSingleton<ServerLooks>();
        services.TryAddSingleton<ServerBoot>();
        services.TryAddSingleton<LogAlerts>();
        services.TryAddSingleton<PeriodicTaskRegistry>();

        // Auth, Activity, Notifications and Suite settings: instance stores over UnitOfWork, stateless
        // themselves, so one shared instance serves every request.
        services.TryAddSingleton<AuthStore>();
        services.TryAddSingleton<ActivityHistoryStore>();
        services.TryAddSingleton<ActivityStore>();
        services.TryAddSingleton<OperationalHistoryStore>();
        services.TryAddSingleton<NotificationChannelStore>();
        services.TryAddSingleton<SuiteSettingsStore>();
        services.TryAddSingleton<SuitePauseService>();
        services.TryAddSingleton<BackupPasswordStore>();
        services.TryAddSingleton<ConfigurationBundleConnections>();
        services.TryAddSingleton<ConfigurationBundleStore>();

        // Processing's core file/library stores (#745 part 5): stateless SQL access over the caller's
        // UnitOfWork, so a singleton is as cheap as a static class was. Reached from Jobs, LibraryMode,
        // MediaManagers and Processing alike, so they live here rather than in any one area's own registration.
        services.TryAddSingleton<LibraryStore>();
        services.TryAddSingleton<FileStateStore>();
        services.TryAddSingleton<FileSkipMarkerStore>();

        // #555: WEIR_CHOWN_OUTPUT/WEIR_FILE_MODE_OUTPUT/WEIR_DIR_MODE_OUTPUT. Windows gets a no-op tools
        // implementation (there is no POSIX owner or mode there) and, when an operator actually set one of these,
        // a one-time warning at startup rather than a silent no-op.
        services.TryAddSingleton<IOutputOwnershipTools>(sp =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return new LinuxOutputOwnershipTools();
            }

            if (options.OutputOwnershipChownEnabled || options.OutputOwnershipFileMode is not null || options.OutputOwnershipDirectoryMode is not null)
            {
                sp.GetRequiredService<ILogger<WindowsOutputOwnershipTools>>().LogWarning(
                    "WEIR_CHOWN_OUTPUT / WEIR_FILE_MODE_OUTPUT / WEIR_DIR_MODE_OUTPUT are set, but Windows has no " +
                    "POSIX owner or mode bits to apply them to. Weir is ignoring them.");
            }

            return new WindowsOutputOwnershipTools();
        });
        services.TryAddSingleton<IOutputOwnership, OutputOwnership>();

        // Who can reach Weir, for System › About (docs/security-hardening.md#windows-firewall). Only the Windows
        // package has a tray to carry a change out; Docker and a bare install are started with their own bind
        // address. A host that never registered its listen address listens the way the server does by default.
        services.TryAddSingleton(new LanAccessFile(options.WeirHome));
        services.TryAddSingleton<INetworkAccess>(sp =>
        {
            var listen = sp.GetService<ServerListenOptions>() ?? new ServerListenOptions(ServerListenOptions.DefaultHost, ServerListenOptions.DefaultPort);
            return OperatingSystem.IsWindows() && UpdateFiles.DetectInstallType(options.RuntimeKind) == "windows"
                ? CreateWindowsNetworkAccess(sp, listen)
                : new UnmanagedNetworkAccess(sp.GetService<ServerRunMode>() ?? ServerRunMode.App, listen);
        });
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static WindowsNetworkAccess CreateWindowsNetworkAccess(IServiceProvider services, ServerListenOptions listen) =>
        new(listen, services.GetRequiredService<LanAccessFile>(), new ComServerFirewall(), new NetworkInterfaceLanAddresses());

    /// <summary>
    /// Hosts every registered <see cref="IPeriodicTask"/>. Added by the job host after startup recovery,
    /// so no periodic work starts before interrupted jobs are recovered.
    /// </summary>
    public static IServiceCollection AddWeirPeriodicTasks(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PeriodicTaskService>());
        return services;
    }
}
