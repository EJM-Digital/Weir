using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Processing;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Startup crash recovery: requeues interrupted jobs and walks library folders for leftovers, in the
/// background, so a slow NAS never holds up Kestrel from listening (#718). No worker claims a job until it
/// finishes; nothing else waits on it.
/// </summary>
public sealed class JobsStartupRecoveryService : IHostedService
{
    private readonly ProcessingJobStore _store;
    private readonly WeirOptions _options;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryStore _libraries;
    private readonly TimeProvider _time;
    private readonly ILogger<JobsStartupRecoveryService> _logger;
    private readonly SwapRecoverySweep? _swapSweep;
    private TaskCompletionSource? _recoveryCompleted;

    public JobsStartupRecoveryService(
        ProcessingJobStore store,
        WeirOptions options,
        LibrarySettingsStore librarySettings,
        LibraryStore libraries,
        TimeProvider time,
        ILogger<JobsStartupRecoveryService> logger,
        SwapRecoverySweep? swapSweep = null)
    {
        _store = store;
        _options = options;
        _librarySettings = librarySettings;
        _libraries = libraries;
        _time = time;
        _logger = logger;
        _swapSweep = swapSweep;
    }

    public StartupRecoveryReport? LastReport { get; private set; }

    /// <summary>The #506 startup sweep's last report, when library mode is registered; null otherwise.</summary>
    public SwapRecoveryReport? LastSwapSweepReport { get; private set; }

    /// <summary>
    /// Completes once recovery and the swap sweep have run, or at once when <see cref="StartAsync"/> was never
    /// called (a unit test building this service directly, with no gate to honour). The file lanes and the
    /// upkeep lane (#717, #718) each await this before claiming their first job, so a pass or a scan never
    /// starts against a library recovery has not yet finished looking at. Nothing else waits on it: <c>/ready</c>
    /// and the web app answer as soon as Kestrel is listening.
    /// </summary>
    public Task RecoveryCompleted => _recoveryCompleted?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Runs recovery and the swap sweep on a thread of their own and returns at once, so their folder walks run
    /// alongside Kestrel starting to listen rather than in front of it (#718). The database calls here finish
    /// without yielding, so without the separate thread the whole walk would run inside this call and hold up
    /// every service started after it. A worker lane must not start before they finish, so it awaits
    /// <see cref="RecoveryCompleted"/> itself.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recoveryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(RunRecoveryAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunRecoveryAsync()
    {
        try
        {
            LastReport = await StartupRecovery.RunAsync(
                _store, _options.WeirHome, _time.GetUtcNow(), TimeSpan.FromSeconds(_options.ProcessingWorkTempStaleSweepMinStaleAgeSeconds), _logger, CancellationToken.None).ConfigureAwait(false);
            await GiveEveryLibraryAProfileAsync(CancellationToken.None).ConfigureAwait(false);
            await WarnAboutReservedLibraryFoldersAsync(CancellationToken.None).ConfigureAwait(false);

            // #506's startup sweep runs after the recovery above and before any worker starts claiming jobs
            // (workers await RecoveryCompleted) — see docs/archive/server-port-notes.md, "Library mode: safe swap".
            if (_swapSweep is not null)
            {
                var folders = await LibraryFoldersForSweepAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    LastSwapSweepReport = await _swapSweep.RunAsync(folders, walkFolders: true, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Startup must not fail because a library-mode sweep could not run.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(exception, "Library mode's startup sweep could not run; interrupted swaps, if any, are picked up at the next start.");
                }
            }
        }
        // Kestrel is already listening by the time this runs (#718), so a failure here must not crash the host —
        // only log loudly — or a worker lane waiting on RecoveryCompleted would wait forever.
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(exception, "Weir startup recovery failed; passes and scans will start without it once Weir is restarted.");
        }
        finally
        {
            _recoveryCompleted!.TrySetResult();
        }
    }

    /// <summary>
    /// Every library has a profile: one without (from an older database or an import) gets the one it was using,
    /// before any worker starts. It changes no rule a file is cleaned by.
    /// </summary>
    private async Task GiveEveryLibraryAProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uow = await UnitOfWork.OpenAsync(_store.Database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var given = await _libraries.GiveEveryLibraryAProfileAsync(uow).ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                if (given > 0)
                {
                    _logger.LogInformation("Gave {Count} workflows the profile they were already using.", given);
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not give every workflow a profile; this is tried again at the next start.");
        }
    }

    /// <summary>
    /// The folder-safety rule (drive roots, Weir's own home, system folders) is validated on save, not on load, so an
    /// existing library that already points at such a folder keeps working. This logs a warning for each one at
    /// startup instead, so an operator with a grandfathered folder finds out without the library being touched.
    /// </summary>
    private async Task WarnAboutReservedLibraryFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uow = await UnitOfWork.OpenAsync(_store.Database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var libraries = await _libraries.ListAsync(uow).ConfigureAwait(false);
                foreach (var library in libraries)
                {
                    foreach (var (label, folder) in new[] { ("watched", library.WatchedFolder), ("work", library.WorkFolder), ("output", library.OutputFolder) })
                    {
                        try
                        {
                            LibraryRules.ValidateFolderPath(label, folder, _options.WeirHome);
                        }
                        catch (ProcessingLibraryException exception)
                        {
                            _logger.LogWarning(
                                "Workflow {LibraryName}'s {Label} folder no longer meets Weir's folder rules: {Reason} It keeps working until the workflow is next saved.",
                                library.Name,
                                label,
                                exception.Message);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not check existing workflows against the folder rules; this is tried again at the next start.");
        }
    }

    private async Task<IReadOnlyList<string>> LibraryFoldersForSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uow = await UnitOfWork.OpenAsync(_store.Database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                return await _librarySettings.AllFoldersAsync(uow).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not read library folders for the startup sweep; it will only look at paths recorded on job rows.");
            return [];
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
