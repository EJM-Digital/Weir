using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Settings;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// In-process worker handler for <c>processing.watched_folder.remux_scan_dispatch.v1</c>: scan the library's
/// watched folder, decide each candidate file's state, and, when asked, enqueue
/// <c>processing.file.remux_pass.v1</c> jobs for it.
/// </summary>
/// <remarks>
/// <para>Per-file attribution to a specific media-manager queue row (path/id/title-year matching a manager's
/// raw JSON to a candidate) is <see cref="ManagerQueueSignals.AttributedRowsForFile"/>, applied through
/// <see cref="WatchedFileDispatch"/> exactly as the "why held" diagnostic applies it.</para>
/// <para>A scan is an independent lane: it reads what it needs, walks and decides with no transaction open, and writes in
/// short batches (<see cref="WatchedFolderScanBatch"/>), so no other lane ever waits on it for the write lock (#708).</para>
/// <para>A workflow a linked Deluno hands its downloads to is never scanned, whatever queued the scan: its files are processed only
/// from Deluno's hand-off (<see cref="Weir.Core.MediaManagers.WorkflowManagerLinks.HandedOffByManager"/>).</para>
/// </remarks>
public sealed class ProcessingWatchedFolderScanDispatchJobHandler : IJobHandler
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _time;
    private readonly WeirOptions _options;
    private readonly ProcessingJobStore _jobStore;
    private readonly MediaManagerConnectionService _managerConnections;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly LibraryStore _libraries;
    private readonly FileStateStore _files;
    private readonly FileSkipMarkerStore _skipMarkers;
    private readonly IFileSystem _fs;

    /// <summary>The workflows whose watched folder the last scan could not see, so the warning is said once and the return once.</summary>
    private readonly ConcurrentDictionary<long, byte> _foldersNotSeen = new();

    private readonly ScanWakeups? _wakeups;
    private readonly ILogger<ProcessingWatchedFolderScanDispatchJobHandler>? _logger;
    private readonly DataChangePublisher? _changes;

    public ProcessingWatchedFolderScanDispatchJobHandler(
        SqliteDatabase database,
        TimeProvider time,
        WeirOptions options,
        ProcessingJobStore jobStore,
        MediaManagerConnectionService managerConnections,
        SuiteSettingsStore suiteSettings,
        LibraryStore libraries,
        FileStateStore files,
        FileSkipMarkerStore skipMarkers,
        ScanWakeups? wakeups = null,
        ILogger<ProcessingWatchedFolderScanDispatchJobHandler>? logger = null,
        DataChangePublisher? changes = null,
        IFileSystem? fileSystem = null)
    {
        _fs = fileSystem ?? PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(managerConnections);
        ArgumentNullException.ThrowIfNull(suiteSettings);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(skipMarkers);
        _database = database;
        _time = time;
        _options = options;
        _jobStore = jobStore;
        _managerConnections = managerConnections;
        _suiteSettings = suiteSettings;
        _libraries = libraries;
        _files = files;
        _skipMarkers = skipMarkers;
        _wakeups = wakeups;
        _logger = logger;
        _changes = changes;
    }

    /// <summary>Test seam: the wait between the first look at a file that was not there and the look that believes it is gone.</summary>
    internal Func<CancellationToken, Task> GoneLookAgain { get; init; } = cancellationToken => Task.Delay(GoneSources.DefaultSettle, cancellationToken);

    public string JobKind => ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = ParsePayload(context.PayloadJson);
        var request = new ScanRequest(
            body.Get("library_id") is WireInteger idValue && idValue.Value > 0 ? (long)idValue.Value : null,
            ProcessingMediaScopes.Normalize(body.Get("media_scope") is WireString scopeStr ? scopeStr.Value : null),
            ScanDispatchJobPayload.NormalizeTrigger(body.Get("scan_trigger") is WireString triggerStr ? triggerStr.Value : "manual"),
            body.Get("enqueue_remux_jobs") is WireValue v && v.IsTruthy);

        var scan = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        if (scan is null)
        {
            return;
        }

        var candidates = await ListCandidatesAsync(scan).ConfigureAwait(false);
        if (candidates is null)
        {
            return;
        }

        var reads = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (reads.ConfigureAwait(false))
        {
            var lookups = await WatchedFolderScanLookups.ReadAsync(reads, _files, _skipMarkers, scan).ConfigureAwait(false);
            var run = new WatchedFolderScanRun(_database, _jobStore, _files, scan, lookups, reads, new ScanPassRequest(context.Id, request.Trigger));
            await run.RunAsync(candidates.Entries, cancellationToken).ConfigureAwait(false);

            // A second after the first hold ends, so the look finds it over. A pass booked for later (#646) carries its own
            // start time and needs no look; this is for files the scan itself holds and for failed files waiting on a retry.
            if (run.EarliestHoldEnds is { } firstEnds)
            {
                _wakeups?.Request(scan.Library.Id, firstEnds + TimeSpan.FromSeconds(1));
            }

            await run.RemoveRejectedFilesAsync(cancellationToken).ConfigureAwait(false);
        }

        // #645: a file that left the watched folder before Weir finished with it says so at once and stops being listed after a
        // while. Only while the watched folder itself can be read, so an unmounted share never empties the list. Done with no
        // transaction open, because a file that is gone is looked at again after a few seconds.
        if (_fs.DirectoryExists(scan.Paths.WatchedFolder))
        {
            await VanishedFiles.SettleAsync(
                _database, scan.Library.Id, scan.Paths.WatchedFolder, scan.MediaScope, scan.Now, ActivityProvenance.ScanTriggerToTrigger[request.Trigger], GoneLookAgain, _jobStore.WakeWorkers, _fs, cancellationToken)
                .ConfigureAwait(false);
        }

        // A look that finds a file still held, or held for a new reason, changes no status and writes no Activity, so only this
        // tells a screen counting down to it that the answer is in.
        _changes?.Publish(DataTopics.LibraryScan);
    }

    /// <summary>
    /// The files in the watched folder, or null when the folder went away after it was seen (a share taken off the network between
    /// the check and the listing). That is the same state as a folder that was not there at the check, so it is said the same way
    /// and the next scan looks again; it is not a failed job. A folder that is still there and cannot be read fails the scan.
    /// </summary>
    private async Task<WatchedFolderScanCandidates?> ListCandidatesAsync(WatchedFolderScan scan)
    {
        try
        {
            return await BlockingWork.RunAsync(() => WatchedFolderListing.Candidates(
                scan.Paths.WatchedFolder,
                scan.Rules.MediaExtensions.Count > 0 ? scan.Rules.MediaExtensions : null,
                scan.Rules.ExcludeMarkers,
                scan.Rules.ExcludeHidden,
                scan.Rules.TopLevelOnly,
                _fs)).ConfigureAwait(false);
        }
        catch (IOException) when (!_fs.DirectoryExists(scan.Paths.WatchedFolder))
        {
            WarnFolderNotSeen(scan.Library);
            return null;
        }
    }

    /// <summary>Said once until the folder is back; the next scan looks again.</summary>
    private void WarnFolderNotSeen(ProcessingLibraryRecord library)
    {
        if (_foldersNotSeen.TryAdd(library.Id, 0))
        {
            _logger?.LogWarning(
                "Did not scan {Library}: Weir can't see its watched folder {Folder}. It may have been deleted or its drive disconnected. Check that the folder exists and that Weir can reach it.",
                library.Name,
                library.WatchedFolder.Trim());
        }
    }

    private sealed record ScanRequest(long? LibraryId, string MediaScope, string Trigger, bool EnqueueRemuxJobs);

    /// <summary>
    /// The library, its folders and rules, what its managers are downloading and whether it may start work now. Reads only,
    /// apart from the settings rows a fresh database creates, and it leaves no transaction open.
    /// </summary>
    private async Task<WatchedFolderScan?> PrepareAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var library = request.LibraryId is { } wantedId ? await _libraries.GetAsync(uow, wantedId).ConfigureAwait(false) : null;
            library ??= await _libraries.SeededForScopeAsync(uow, request.MediaScope).ConfigureAwait(false);
            if (library is null)
            {
                var label = request.MediaScope == ProcessingMediaScopes.Tv ? "TV" : "Movies";
                throw new InvalidOperationException($"No workflow covers {label}. Add one in Setup › Workflows, then queue this work again.");
            }

            var links = await _libraries.ManagerLinksAsync(uow, library.Id).ConfigureAwait(false);
            if (links.HandedOffByManager)
            {
                // However this scan was queued, it queues nothing: a Deluno-linked workflow is processed only from Deluno's hand-off.
                _logger?.LogInformation("Did not scan {Library}: {Reason}", library.Name, links.ScanSkippedReason);
                return null;
            }

            if (library.WatchedFolder.Trim() is { Length: > 0 } saved)
            {
                if (!_fs.DirectoryExists(ProcessingLibraryFolders.ExpandForFilesystem(saved)))
                {
                    // Not a failed job to retry: the folder is gone (deleted, or its drive unmounted) and a retry would find the same. This
                    // is a real problem, so it is a warning in plain words, said once until the folder is back; the next scan looks again.
                    WarnFolderNotSeen(library);
                    return null;
                }

                if (_foldersNotSeen.TryRemove(library.Id, out _))
                {
                    _logger?.LogInformation("The watched folder {Folder} for {Library} is back, so Weir is scanning it again.", saved, library.Name);
                }
            }

            var (paths, pathError) = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, _options.WeirHome, _fs);
            if (paths is null)
            {
                throw new InvalidOperationException(pathError ?? "Path settings are incomplete for this scan.");
            }

            // Manager queue signals: ask every manager linked to this library, and note who did not answer. Always the
            // library's own linked connections, even when that is none: an empty selector means "ask nobody", not "fall back
            // to every connection for the scope".
            var connectionIds = await _libraries.ManagerConnectionIdsAsync(uow, library.Id).ConfigureAwait(false);
            var signals = await _managerConnections.CollectQueueSignalsAsync(uow, request.MediaScope, connectionIds, cancellationToken).ConfigureAwait(false);

            var suite = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);

            return new WatchedFolderScan(
                library,
                request.MediaScope,
                paths,
                LibraryAdmissionRules.For(library),
                signals,
                AdmissionWindow(library, suite, now),
                request.EnqueueRemuxJobs,
                now,
                links,
                _fs);
        }
    }

    private static ScanAdmissionWindow AdmissionWindow(ProcessingLibraryRecord library, SuiteSettingsRecord suite, DateTimeOffset now)
    {
        var pause = PauseState.Resolve(suite, now.UtcDateTime);
        var timezoneName = string.IsNullOrWhiteSpace(suite.AppTimezone) ? "UTC" : suite.AppTimezone.Trim();
        var admissionSnapshot = new LibraryAdmissionSnapshot(
            library.Id, library.Enabled, library.ScheduleEnabled, library.ScheduleGrid, library.ScheduleHoursLimited,
            library.ScheduleDays, library.ScheduleStart, library.ScheduleEnd, library.MaxConcurrentFiles);
        var inWindow = WorkAdmissionRules.LibraryWindowOpen(admissionSnapshot, timezoneName, now);
        return new ScanAdmissionWindow(
            inWindow,
            pause.Paused ? pause.Reason : null,
            pause.Paused && pause.PausedUntil is { } until ? new DateTimeOffset(until.AsUtc) : null,
            inWindow ? null : WorkAdmissionRules.LibraryWindowReopensAt(admissionSnapshot, timezoneName, now));
    }

    private static WireObject ParsePayload(string? payloadJson)
    {
        var raw = (payloadJson ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return new WireObject();
        }

        WireValue data;
        try
        {
            data = WireJsonParser.Parse(raw);
        }
        catch (WireJsonDecodeException exception)
        {
            throw new ArgumentException("watched-folder remux scan dispatch payload must be a JSON object", exception);
        }

        if (data is not WireObject dict)
        {
            throw new ArgumentException("watched-folder remux scan dispatch payload must be a JSON object");
        }

        return dict;
    }
}
