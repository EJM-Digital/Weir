using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Core.Text;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// Worker handler for <see cref="LibraryModeJobKinds.ScanKind"/> (#505 point 2): walks a library's library folders,
/// probes new or changed files (cached by path, size and mtime), and classifies each against the library's rules with the
/// exact same engine the download pipeline plans with. Read-only: no file is written or moved by a scan, and only a scheduled
/// scan (the "Scheduled scan and clean" switch) queues cleans, once it knows what the files hold.
/// </summary>
/// <remarks>
/// Title matching (#551, "Blade Runner 2049 (Radarr)"): for every enabled connection covering the library's media scope,
/// <c>IMediaManagerPort.ListLibraryFilesAsync</c> (#507) is asked which files it knows, then each is matched to one of this
/// scan's own walked files with <see cref="LibraryTitleMatcher.MatchLocalPath"/> (a direct path comparison, or the reverse
/// of the local-&gt;manager path translation #507's notify step already uses). A manager that cannot be reached is recorded
/// as a scan error ("couldn't ask Radarr...") rather than failing the scan; its files, like any file no connection claims,
/// stay unmatched and processable (#505 explicitly allows this).
/// </remarks>
public sealed class LibraryScanHandler : IJobHandler
{
    /// <summary>The most often the screens are told a scan has looked at more files or indexed more of them, however fast they come.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly SqliteDatabase _database;
    private readonly MediaTools _tools;
    private readonly MediaManagerConnectionService _connections;
    private readonly IHardlinkInspector _hardlinks;
    private readonly ProcessingJobStore _jobs;
    private readonly RedownloadRiskChecker _riskChecker;
    private readonly LibraryScanStore _scans;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryFileMarksStore _fileMarks;
    private readonly LibraryViewStore _libraryView;
    private readonly LibraryStore _libraries;
    private readonly LibraryScanProgress _progress;
    private readonly TimeProvider _time;
    private readonly DataChangePublisher? _changes;

    public LibraryScanHandler(
        SqliteDatabase database,
        MediaTools tools,
        MediaManagerConnectionService connections,
        IHardlinkInspector hardlinks,
        ProcessingJobStore jobs,
        RedownloadRiskChecker riskChecker,
        LibraryScanStore scans,
        LibrarySettingsStore librarySettings,
        LibraryFileMarksStore fileMarks,
        LibraryViewStore libraryView,
        LibraryStore libraries,
        LibraryScanProgress progress,
        TimeProvider time,
        ILogger<LibraryScanHandler> logger,
        DataChangePublisher? changes = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _hardlinks = hardlinks ?? throw new ArgumentNullException(nameof(hardlinks));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _riskChecker = riskChecker ?? throw new ArgumentNullException(nameof(riskChecker));
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _fileMarks = fileMarks ?? throw new ArgumentNullException(nameof(fileMarks));
        _libraryView = libraryView ?? throw new ArgumentNullException(nameof(libraryView));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _changes = changes;
        // Kept in the constructor for DI symmetry with LibraryCleanHandler; nothing here logs yet.
        ArgumentNullException.ThrowIfNull(logger);
    }

    public string JobKind => LibraryModeJobKinds.ScanKind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await ScanAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progress.Clear(context.Id);
        }
    }

    private async Task ScanAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        long libraryId;
        string trigger;
        ProcessingLibraryRecord? library;
        LibrarySettings settings;
        ProcessingRulesConfig rules;
        IReadOnlyList<LibraryScanFileEntry> previousFiles;
        IReadOnlyDictionary<string, LibraryFileMark> marks;
        List<ManagerConnection> connections;
        await using (uow.ConfigureAwait(false))
        {
            WireObject payload;
            try
            {
                payload = string.IsNullOrWhiteSpace(context.PayloadJson) ? new WireObject() : WireJsonParser.Parse(context.PayloadJson) as WireObject ?? new WireObject();
            }
            catch (WireJsonDecodeException)
            {
                payload = new WireObject();
            }

            libraryId = payload.Get("library_id") is WireInteger idValue ? (long)idValue.Value : 0;
            trigger = payload.Get("trigger") is WireString { Value.Length: > 0 } triggerValue ? triggerValue.Value : "manual";
            library = libraryId > 0 ? await _libraries.GetAsync(uow, libraryId).ConfigureAwait(false) : null;
            if (library is null)
            {
                await _scans.RecordResultAsync(uow, context.Id, new LibraryScanOutcome(_time.GetUtcNow(), []), false, "This workflow no longer exists.")
                    .ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                await LibraryFileIndexWriter.ReplaceAsync(_database, libraryId, [], cancellationToken).ConfigureAwait(false);
                return;
            }

            settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
            if (settings.Folders.Count == 0)
            {
                await _scans.RecordResultAsync(
                        uow, context.Id, new LibraryScanOutcome(_time.GetUtcNow(), []), false,
                        "This library has no folders yet. Add one under Library setup on the Library page, then check again.")
                    .ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                await LibraryFileIndexWriter.ReplaceAsync(_database, libraryId, [], cancellationToken).ConfigureAwait(false);
                return;
            }

            rules = await LibraryModeRules.ForAsync(uow, _libraries, library, settings).ConfigureAwait(false);
            previousFiles = await _scans.CurrentFilesAsync(uow, libraryId).ConfigureAwait(false);
            marks = await _fileMarks.ForLibraryAsync(uow, libraryId).ConfigureAwait(false);
            connections = await _connections.ConnectionsForScopeAsync(uow, library.MediaType).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }

        var previousByPath = previousFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);

        var entries = new List<LibraryScanFileEntry>();
        var errors = new List<string>();
        var progress = new ThrottledDataChange(_changes, DataTopics.LibraryScan, ProgressInterval, _time);
        foreach (var walked in LibraryFileWalker.Walk(library, settings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classified = await ClassifyOneAsync(walked, rules, previousByPath, cancellationToken).ConfigureAwait(false);
            entries.Add(classified with { ChangeReason = ChangeReasonFor(classified, previousFiles.Count > 0, previousByPath, marks) });
            _progress.Report(context.Id, entries.Count);
            progress.Publish();
        }

        // #551: match each walked file to the title a linked Sonarr/Radarr connection already knows it under.
        errors.AddRange(await MatchManagerTitlesAsync(connections, library.MediaType, settings.Folders, entries, cancellationToken).ConfigureAwait(false));

        var outcome = new LibraryScanOutcome(_time.GetUtcNow(), errors);
        var wouldChange = entries.Count(e => e.Classification == LibraryFileClassification.WouldChange);
        var cannotProcess = entries.Count(e => e.Classification == LibraryFileClassification.CannotProcess);

        // "Scheduled scan and clean": a scheduled scan goes on to clean what it found, judged exactly as Clean on the
        // Library screen judges a selection. The preflight asks media managers over the network, so it runs here, before
        // the write below, never while holding it.
        var scheduled = trigger == LibraryModeSchedule.Trigger && settings.ScheduleEnabled;
        var toClean = new List<LibraryScanFileEntry>();
        var preflight = new List<LibraryFilePreflightResult>();
        if (scheduled)
        {
            toClean = entries
                .Where(e => e.Classification == LibraryFileClassification.WouldChange && !(marks.TryGetValue(e.Path, out var mark) && mark.LeaveAlone))
                .ToList();
            var connectionsById = connections.Where(c => c.ConnectionId is not null).DistinctBy(c => c.ConnectionId).ToDictionary(c => c.ConnectionId!.Value);
            preflight = await LibraryCleanPreflightRunner.RunAsync(
                    toClean, settings, rules, library.MediaType, _hardlinks, _riskChecker, connectionsById, cancellationToken)
                .ConfigureAwait(false);
        }

        await LibraryFileIndexWriter.ReplaceAsync(_database, libraryId, entries, cancellationToken, progress.Publish).ConfigureAwait(false);
        await using (var recordUow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false))
        {
            await _scans.RecordResultAsync(recordUow, context.Id, outcome, true, null).ConfigureAwait(false);
            var queued = scheduled ? await QueueScheduledCleansAsync(recordUow, libraryId, toClean, preflight).ConfigureAwait(false) : 0;
            var title = $"Scanned {library.Name}: {Plural.Of(entries.Count, "file")}, {wouldChange} would change, {cannotProcess} could not be processed";
            if (scheduled)
            {
                title += $"; {Plural.Of(queued, "file")} queued to be cleaned";
            }

            await SqliteActivityWriter.RecordAsync(
                    recordUow,
                    new ActivityEventDraft(
                        LibraryActivityEventTypes.ScanCompleted,
                        "library",
                        title,
                        WireJsonWriter.Dumps(
                            new WireObject().Set("trigger", trigger).Set("library_id", libraryId).Set("result", "success").Set("queued_to_clean", queued),
                            WireJsonFormat.Compact)))
                .ConfigureAwait(false);
            await recordUow.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues a clean for each file a scheduled scan found that the rules would change, has not been set aside, and passed
    /// the preflight. The removal was confirmed when the schedule was turned on (#505 point 7), which is why the jobs carry
    /// that confirmation; a file still shared with a download, or that a manager would download again, is never queued,
    /// exactly as with Clean. Nothing is queued if the schedule was turned off while the scan ran. Returns how many were.
    /// </summary>
    private async Task<int> QueueScheduledCleansAsync(
        UnitOfWork uow, long libraryId, List<LibraryScanFileEntry> toClean, List<LibraryFilePreflightResult> preflight)
    {
        if (!(await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false)).ScheduleEnabled)
        {
            return 0;
        }

        // What the preflight found is recorded for the Problems view, as Clean records it.
        foreach (var result in preflight)
        {
            await _libraryView.RecordPreflightProblemAsync(uow, libraryId, result.FilePath, result.ProblemKind).ConfigureAwait(false);
        }

        var skipped = preflight.Where(r => r.Skip).Select(r => r.FilePath).ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var entry in toClean.Where(e => !skipped.Contains(e.Path)))
        {
            await _scans.EnqueueCleanAsync(uow, _jobs, libraryId, entry.Path, LibraryModeSchedule.Trigger, confirmFinalRemoval: true, probeJson: entry.ProbeJson)
                .ConfigureAwait(false);
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// Why a file the rules would change needs cleaning, judged against what the last scan knew of it and what Weir last did
    /// to it (see <see cref="LibraryChangeReasons.Decide"/>). It is kept on the file's row and carried forward by each scan
    /// until the file stops needing it.
    /// </summary>
    private static string? ChangeReasonFor(
        LibraryScanFileEntry now,
        bool hadEarlierScan,
        Dictionary<string, LibraryScanFileEntry> previousByPath,
        IReadOnlyDictionary<string, LibraryFileMark> marks)
    {
        var before = previousByPath.TryGetValue(now.Path, out var known)
            ? new LibraryChangeReasons.Before(known.SizeBytes, known.ModifiedTimeUnixSeconds, known.Classification, known.ChangeReason)
            : null;
        long? cleanedAt = marks.TryGetValue(now.Path, out var mark) ? mark.CleanedAt?.ToUnixTimeSeconds() : null;
        return LibraryChangeReasons.Decide(now.Classification, hadEarlierScan, before, now.SizeBytes, now.ModifiedTimeUnixSeconds, cleanedAt);
    }

    private async Task<LibraryScanFileEntry> ClassifyOneAsync(
        LibraryWalkedFile walked,
        ProcessingRulesConfig rules,
        Dictionary<string, LibraryScanFileEntry> previousByPath,
        CancellationToken cancellationToken)
    {
        // #568: how many names share this file's data, recorded per scan so the Problems view can group seeding
        // files in SQL instead of stat()-ing a whole library on every page load. Unknown stays null (see
        // HardlinkPolicy: unknown is never treated as evidence of sharing).
        var linkCount = LinkCountOf(walked.Path);

        string probeJson;
        if (previousByPath.TryGetValue(walked.Path, out var cached) && cached.MatchesFile(walked.Path, walked.SizeBytes, walked.ModifiedTimeUnixSeconds) && cached.ProbeJson is { Length: > 0 })
        {
            probeJson = cached.ProbeJson;
        }
        else
        {
            try
            {
                var probed = await _tools.FfprobeJsonAsync(walked.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
                probeJson = probed.GetRawText();
            }
            catch (Exception exception) when (exception is MediaToolException or MediaToolTimeoutException)
            {
                return new LibraryScanFileEntry(
                    walked.Path, walked.SizeBytes, walked.ModifiedTimeUnixSeconds, LibraryFileClassification.CannotProcess,
                    null, ToolFailureText.Plain(exception), 0, 0, null, null, null,
                    ProblemKind: ProblemKindForReadFailure(walked.Path, exception), LinkCount: linkCount);
            }
        }

        var classification = LibraryFilePlanner.Classify(ProbeResult.Parse(probeJson), rules);

        return new LibraryScanFileEntry(
            walked.Path,
            walked.SizeBytes,
            walked.ModifiedTimeUnixSeconds,
            classification.Classification,
            classification.Summary,
            classification.Reason,
            classification.RemovedAudioCount,
            classification.RemovedSubtitleCount,
            null,
            null,
            probeJson,
            classification.EstimatedBytesSaved,
            ProblemKind: classification.ProblemKind,
            LinkCount: linkCount);
    }

    private int? LinkCountOf(string path)
    {
        try
        {
            return _hardlinks.LinkCount(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells "Weir is not allowed to open this" apart from "this file is damaged", since #568's Problems view
    /// gives each a different thing to do (fix the permissions, versus download the title again). The evidence is
    /// whether Weir itself can open the file for reading right now, not the tool's wording, which varies by build.
    /// </summary>
    private static LibraryProblemKind ProblemKindForReadFailure(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LibraryProblemKind.Unreadable;
        }
        catch (UnauthorizedAccessException)
        {
            return LibraryProblemKind.NoPermission;
        }
        catch (Exception open) when (open is IOException)
        {
            return LibraryProblemKind.Unreadable;
        }
    }

    /// <summary>
    /// #551: asks every connection covering this library's scope which files it knows, matches each to one of
    /// <paramref name="entries"/>' own paths, and mutates the first match's manager fields in place (a path
    /// already claimed by an earlier connection is left alone — first reported match wins). Returns one plain
    /// note per connection Weir could not ask, for the scan's own error list ("couldn't ask Radarr..."); an
    /// unreachable manager never fails the scan, and every file it would have covered simply stays unmatched.
    /// </summary>
    private async Task<List<string>> MatchManagerTitlesAsync(
        List<ManagerConnection> connections,
        string mediaScope,
        IReadOnlyList<string> localFolders,
        List<LibraryScanFileEntry> entries,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        if (entries.Count == 0 || connections.Count == 0)
        {
            return notes;
        }

        var localPaths = entries.Select(e => e.Path).ToList();
        var matchedPaths = new HashSet<string>(StringComparer.Ordinal);
        var indexByPath = entries
            .Select((entry, index) => (entry.Path, index))
            .ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.Ordinal);

        foreach (var connection in connections)
        {
            // The connections are those whose kind has a port (ConnectionsForScopeAsync), and a port answers a manager that
            // does not answer with an Unreachable signal rather than by throwing.
            var port = _connections.Ports.PortForKind(connection.Kind)!;
            var signal = await port.ListLibraryFilesAsync(connection, mediaScope, cancellationToken).ConfigureAwait(false);

            if (signal.Status == SignalStatus.Unreachable)
            {
                notes.Add($"Weir couldn't ask {connection.Label} which titles it manages: {signal.Detail}");
                continue;
            }

            if (signal.Status != SignalStatus.Reported || signal.Files.Count == 0)
            {
                continue;
            }

            var description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
            var managerLibraries = description.Status == SignalStatus.Reported ? description.Libraries : [];

            foreach (var file in signal.Files)
            {
                var localPath = LibraryTitleMatcher.MatchLocalPath(file.FilePath, localPaths, managerLibraries, localFolders);
                if (localPath is null || !matchedPaths.Add(localPath))
                {
                    continue;
                }

                var index = indexByPath[localPath];
                entries[index] = entries[index] with
                {
                    ManagerKind = connection.Kind,
                    ManagerTitle = file.TitleName,
                    ManagerConnectionId = connection.ConnectionId,
                    ManagerTitleId = file.TitleId,
                    ManagerFileId = file.FileId,
                    ManagerQualityProfileId = file.QualityProfileId,
                };
            }
        }

        return notes;
    }
}
