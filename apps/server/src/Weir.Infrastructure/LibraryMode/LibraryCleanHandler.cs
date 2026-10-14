using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Library;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// Worker handler for <see cref="LibraryModeJobKinds.CleanKind"/> (#505 point 8): probes and plans one library file exactly
/// as the scan did, then — when the plan actually changes something — remuxes it to a temp file beside the original
/// (<c>mode: library</c>) and hands it to the #506 safe swap. Never calls <see cref="IFailurePolicy"/>: a library failure is
/// recorded and reported, but never queues a reject or pass-through job, and the original is always left untouched on
/// anything short of a committed swap.
/// </summary>
public sealed partial class LibraryCleanHandler : IJobHandler
{
    private const long BytesPerMib = 1024 * 1024;

    private readonly SqliteDatabase _database;
    private readonly MediaTools _tools;
    private readonly SafeSwap _swap;
    private readonly ILibraryFileChangeNotifier _notifier;
    private readonly IHardlinkInspector _hardlinkInspector;
    private readonly IRemovedTrackStore _removedTrackStore;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryFileMarksStore _fileMarks;
    private readonly LibraryStore _libraries;
    private readonly DataChangePublisher _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<LibraryCleanHandler> _logger;

    public LibraryCleanHandler(
        SqliteDatabase database,
        MediaTools tools,
        SafeSwap swap,
        ILibraryFileChangeNotifier notifier,
        IHardlinkInspector hardlinkInspector,
        IRemovedTrackStore removedTrackStore,
        LibrarySettingsStore librarySettings,
        LibraryFileMarksStore fileMarks,
        LibraryStore libraries,
        DataChangePublisher changes,
        TimeProvider time,
        ILogger<LibraryCleanHandler> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _swap = swap ?? throw new ArgumentNullException(nameof(swap));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _hardlinkInspector = hardlinkInspector ?? throw new ArgumentNullException(nameof(hardlinkInspector));
        _removedTrackStore = removedTrackStore ?? throw new ArgumentNullException(nameof(removedTrackStore));
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _fileMarks = fileMarks ?? throw new ArgumentNullException(nameof(fileMarks));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string JobKind => LibraryModeJobKinds.CleanKind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        WireObject payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(context.PayloadJson) ? new WireObject() : WireJsonParser.Parse(context.PayloadJson) as WireObject ?? new WireObject();
        }
        catch (WireJsonDecodeException)
        {
            payload = new WireObject();
        }

        var libraryId = payload.Get("library_id") is WireInteger idValue ? (long)idValue.Value : 0;
        var path = payload.Get("path") is WireString { Value.Length: > 0 } pathValue ? pathValue.Value : string.Empty;
        var trigger = payload.Get("trigger") is WireString { Value.Length: > 0 } triggerValue ? triggerValue.Value : "manual";
        var confirmed = payload.Get("confirm_final_removal") is WireBool { Value: true };
        var inUseAttempts = payload.Get("in_use_attempts") is WireInteger attemptsValue ? (int)attemptsValue.Value : 0;

        if (path.Length == 0)
        {
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, "This job's payload has no file path.").ConfigureAwait(false);
            return;
        }

        ProcessingLibraryRecord? library;
        ProcessingRulesConfig rules;
        LibrarySettings settings;
        bool leftAlone;
        await using (var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false))
        {
            library = libraryId > 0 ? await _libraries.GetAsync(uow, libraryId).ConfigureAwait(false) : null;
            if (library is null)
            {
                await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, "This workflow no longer exists.").ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                return;
            }

            settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
            rules = await LibraryModeRules.ForAsync(uow, _libraries, library, settings).ConfigureAwait(false);
            leftAlone = await _fileMarks.IsLeftAloneAsync(uow, libraryId, path).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }

        if (leftAlone)
        {
            await RecordAsync(
                libraryId,
                path,
                trigger,
                LibraryActivityEventTypes.FileSkipped,
                "You asked Weir to leave this file alone, so it was not cleaned.").ConfigureAwait(false);
            return;
        }

        // #508 step 1: a file another name still shares data with (almost always a download client still seeding
        // it) is skipped before any read/plan/write work, unless the library allows cleaning hardlinked files. An
        // unreadable link count (no such file, an unsupported volume) is treated as "cannot tell", which
        // HardlinkPolicy already resolves to "do not block" rather than guessing.
        int? linkCount;
        try
        {
            linkCount = _hardlinkInspector.LinkCount(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            linkCount = null;
        }

        var hardlinkDecision = HardlinkPolicy.Evaluate(linkCount, settings.CleanHardlinkedFiles);
        if (hardlinkDecision.Skip)
        {
            var preflight = LibraryCleanPreflight.Evaluate(path, hardlinkDecision, redownloadRisk: null);
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileSkipped, string.Join(" ", preflight.SkipReasons)).ConfigureAwait(false);
            return;
        }

        ProbeResult probe;
        IReadOnlyList<string> sourceWarnings;
        try
        {
            var probed = await _tools.ProbeWithWarningsAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            probe = ProbeResult.Parse(probed.Probe.GetRawText());
            sourceWarnings = probed.Warnings;
        }
        catch (Exception exception) when (exception is MediaToolException or MediaToolTimeoutException)
        {
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, ToolFailureText.Plain(exception)).ConfigureAwait(false);
            return;
        }

        await RecordMeasuredCostAsync(context.Id, probe, cancellationToken).ConfigureAwait(false);
        LibraryFilePlanResult plan;
        if (ManualPlanJson.FromPyJson(payload.Get("manual_plan")) is { } choice)
        {
            var chosen = PlanFromChoice(probe, choice, payload.Get("expected_size_bytes"), path);
            if (chosen.Problem is { } problem)
            {
                await RecordAsync(libraryId, path, trigger, problem.EventType, problem.Message).ConfigureAwait(false);
                return;
            }

            plan = chosen.Plan!;
        }
        else
        {
            try
            {
                plan = LibraryFilePlanner.Classify(probe, rules);
            }
            catch (RulesInputException)
            {
                await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, ToolFailureText.UnusableTrackData).ConfigureAwait(false);
                return;
            }
        }

        if (plan.Classification == LibraryFileClassification.Matches)
        {
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileSkipped, "This file already matches the workflow's rules; nothing to clean.").ConfigureAwait(false);
            return;
        }

        if (plan.Classification == LibraryFileClassification.CannotProcess)
        {
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, plan.Reason ?? "Weir cannot process this file.").ConfigureAwait(false);
            return;
        }

        var removesTracks = plan.RemovedAudioCount + plan.RemovedSubtitleCount > 0;
        if (removesTracks && !confirmed)
        {
            // Belt and braces: the API refuses this before the job is ever queued (400 without confirm_final_removal).
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, "Removing tracks was not confirmed, so Weir left this file untouched.").ConfigureAwait(false);
            return;
        }

        double? durationSeconds = null;
        if (probe.Json.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationText) &&
            double.TryParse(durationText.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedDuration))
        {
            durationSeconds = parsedDuration;
        }

        var keepOriginal = settings.KeepOriginalAfterClean
            ? new KeepOriginalOptions(settings.Folders, settings.OriginalsFolder)
            : null;
        // The workflow's "Writes files with" and FFmpeg compatibility choices apply here exactly as they do to new downloads.
        var writer = _tools.WriterFor(path, library.RemuxWriter);
        var ffmpegInputFlags = FfmpegStrictnessLevels.InputFlags(library.FfmpegStrictness);
        string? writerNote = null;
        var result = await _swap.RunAsync(
            context.Id,
            path,
            async (tempPath, ct) =>
            {
                var workDir = Path.GetDirectoryName(tempPath) is { Length: > 0 } dir ? dir : ".";
                var staged = await _tools.RemuxToTempFileAsync(path, workDir, plan.Plan!, probe.Json, sourceWarnings, durationSeconds: durationSeconds, ffmpegInputFlags: ffmpegInputFlags, writer: writer, cancellationToken: ct).ConfigureAwait(false);
                writerNote = FfmpegFallbackNote(library.RemuxWriter, staged);
                try
                {
                    File.Move(staged.Path, tempPath, overwrite: false);
                }
                catch
                {
                    TryDelete(staged.Path);
                    throw;
                }
            },
            SwapOptions.Default with
            {
                OriginalDurationSeconds = durationSeconds,
                KeepOriginal = keepOriginal,
                KeepFreeBytes = library.MinimumFreeDiskSpaceMb * BytesPerMib,
                AllowHardlinked = settings.CleanHardlinkedFiles,
            },
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case SwapOutcome.Committed:
                await OnCommittedAsync(library, path, plan, result, writerNote, cancellationToken).ConfigureAwait(false);
                return;
            case SwapOutcome.InUse:
                await OnInUseAsync(context, payload, libraryId, path, trigger, inUseAttempts).ConfigureAwait(false);
                return;
            case SwapOutcome.InsufficientSpace:
                await OnWaitingForSpaceAsync(context, payload, libraryId, path, trigger, result.Message).ConfigureAwait(false);
                return;
            default:
                await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, result.Message).ConfigureAwait(false);
                return;
        }
    }

}
