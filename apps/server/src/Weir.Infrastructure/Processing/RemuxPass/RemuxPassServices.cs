using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Metrics;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// A handler crash is still a failure, recorded against the file with the
/// retry policy applied, and the library's failure policy acts on it so the file is not stranded (#465).
/// </summary>
public sealed class RemuxPassFailureRecorder : IUnhandledJobFailureRecorder
{
    private readonly SqliteDatabase _database;
    private readonly IFailurePolicy _policy;
    private readonly LibraryStore _libraries;
    private readonly TimeProvider _time;
    private readonly ILogger<RemuxPassFailureRecorder> _logger;

    public RemuxPassFailureRecorder(
        SqliteDatabase database, IFailurePolicy policy, LibraryStore libraries, TimeProvider time, ILogger<RemuxPassFailureRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _policy = policy;
        _libraries = libraries;
        _time = time;
        _logger = logger;
    }

    public Task<bool?> RecordAsync(UnhandledJobFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return LockedWrites.RunAsync<bool?>(
            _database,
            async uow =>
            {
                var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, failure.LibraryId, failure.MediaScope).ConfigureAwait(false);
                if (library is null || failure.RelativeMediaPath is null)
                {
                    return null;
                }

                var decision = await RemuxPassFileState.RecordFailureAsync(uow, library, failure.RelativeMediaPath, ProcessingFailureClasses.Unknown, failure.Message, _time.GetUtcNow())
                    .ConfigureAwait(false);
                WireObject? origin = null;
                try
                {
                    origin = WireJsonParser.Parse(failure.Context.PayloadJson ?? "{}") is WireObject payload ? payload.Get("origin") as WireObject : null;
                }
                catch (WireJsonDecodeException exception)
                {
                    // The failure is still recorded and the policy still applied, only without the hand-off origin.
                    _logger.LogWarning(exception, "Unhandled-failure recorder could not read the payload of job_id={JobId}; applying the failure policy without its origin.", failure.Context.Id);
                }

                await HandoffRetries.QueueIfOwedAsync(uow, _policy, _libraries, library, failure.RelativeMediaPath, origin, decision).ConfigureAwait(false);
                await _policy.ApplyFailurePolicyAsync(uow, library, failure.RelativeMediaPath, decision.WillRetry, origin, badRelease: false).ConfigureAwait(false);
                return decision.WillRetry;
            },
            _logger,
            "unhandled failure",
            cancellationToken);
    }
}

/// <summary>Registers the remux pass (#522 part 3): its handler, the failure recorder and the seams it runs through.</summary>
public static class RemuxPassServices
{
    public static IServiceCollection AddWeirRemuxPass(this IServiceCollection services, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWeirPlatform(options);
        services.AddWeirMediaManagers(options);
        services.AddWeirMediaTools();
        services.TryAddSingleton<SqliteRemuxPassData>();
        services.TryAddSingleton<IRemuxPassFileFacts>(sp => sp.GetRequiredService<SqliteRemuxPassData>());
        services.TryAddSingleton<IPostSuccessCleanupData>(sp => sp.GetRequiredService<SqliteRemuxPassData>());
        services.TryAddSingleton<ITvSeasonFolderCleanup, TvSeasonFolderCleanup>();
        // Seam: the failure policy's follow-up handlers (pass-through and reject) are
        // registered by AddWeirProcessingFailureFollowUps, which calls this method rather than duplicating it.
        services.TryAddSingleton<IFailurePolicy, QueueingFailurePolicy>();
        services.TryAddSingleton<IOriginalLanguageLookup, GatewayOriginalLanguageLookup>();
        services.TryAddSingleton(new RemuxPassSettings
        {
            ProbeSizeMb = options.ProcessingProbeSizeMb,
            AnalyzeDurationSeconds = options.ProcessingAnalyzeDurationSeconds,
            MovieOutputCleanupMinAgeSeconds = options.ProcessingMovieOutputCleanupMinAgeSeconds,
            TvOutputCleanupMinAgeSeconds = options.ProcessingTvOutputCleanupMinAgeSeconds,
        });
        services.TryAddSingleton(sp => new RemuxPassRunner(
            sp.GetRequiredService<MediaTools>(),
            sp.GetRequiredService<IMediaToolResolver>(),
            sp.GetRequiredService<IRemuxPassFileFacts>(),
            sp.GetRequiredService<IPostSuccessCleanupData>(),
            sp.GetRequiredService<ITvSeasonFolderCleanup>(),
            sp.GetRequiredService<IOriginalLanguageLookup>(),
            sp.GetRequiredService<RemuxPassSettings>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RemuxPassRunner>>(),
            sp.GetService<RuntimeMetricsStore>(),
            sp.GetService<IOutputOwnership>()));
        services.TryAddSingleton(sp => new RemuxPassHandler(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<WeirOptions>(),
            sp.GetRequiredService<RemuxPassRunner>(),
            sp.GetRequiredService<IFailurePolicy>(),
            sp.GetRequiredService<OperatorSettingsStore>(),
            sp.GetRequiredService<HandbackStore>(),
            sp.GetRequiredService<LibraryStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RemuxPassHandler>>(),
            sp.GetRequiredService<DownloadedScanNotifier>(),
            sp.GetService<HandoffCompletionReporter>(),
            sp.GetService<ProcessingJobStore>(),
            sp.GetRequiredService<LiveProgressStore>(),
            sp.GetService<ScanWakeups>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, RemuxPassHandler>(sp => sp.GetRequiredService<RemuxPassHandler>()));
        // Replaces the jobs module's no-op recorder, whichever registration runs first.
        services.Replace(ServiceDescriptor.Singleton<IUnhandledJobFailureRecorder, RemuxPassFailureRecorder>());
        return services;
    }
}
