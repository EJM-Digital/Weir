using Microsoft.Data.Sqlite;
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
using Weir.Core.Text;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// <see cref="LibraryCleanHandler"/>'s post-swap concern: what happens once <see cref="SafeSwap.RunAsync"/> has
/// returned — committed bookkeeping, the in-use retry backoff, and the shared Activity-recording and formatting
/// helpers both outcomes use.
/// </summary>
public sealed partial class LibraryCleanHandler
{
    private async Task OnCommittedAsync(ProcessingLibraryRecord library, string path, LibraryFilePlanResult plan, SwapResult result, string? writerNote, CancellationToken cancellationToken)
    {
        // #509 step 1: record what this clean removed for good, keyed the same way library mode identifies the
        // file everywhere else (library id + this path). A future rule change can then ask #509's diff whether
        // any of it would now be kept. Best-effort: a store failure here must never undo an already-committed swap.
        if (plan.Plan is { RemovedTrackRecords.Count: > 0 } committedPlan)
        {
            try
            {
                var key = new RemovedTrackFileKey(library.Id, path);
                await _removedTrackStore.RecordAsync(key, committedPlan.RemovedTrackRecords, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort, like the notify step below: recording removed tracks must never fail a committed clean.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Library clean committed but recording its removed tracks (#509) failed.");
            }
        }

        // #507 (telling the manager) records its own Activity entries for a notify failure or warning; this handler's own
        // "cleaned" entry below never depends on how that call went.
        try
        {
            var reason = RemovedTracks(plan.RemovedAudioCount, plan.RemovedSubtitleCount);
            await _notifier.NotifyAsync(new LibraryFileChange(library.MediaType, path, Reason: reason), cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The notify step is best-effort and must never fail a committed clean.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library clean committed but the manager notify step (#507) failed to run.");
        }

        // The scan rewrites its index from scratch, so "Weir cleaned this" is kept where a rescan cannot reach it. The index is also only
        // as new as the last scan, which would go on listing this file as needing the clean it has just had, so the library is looked at again.
        try
        {
            await using var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await _fileMarks.MarkCleanedAsync(uow, library.Id, path, _time.GetUtcNow()).ConfigureAwait(false);
            await RequestScanAsync(uow, library.Id).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort, like the steps above: a committed clean is never undone by bookkeeping.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library clean committed but recording that it was cleaned failed.");
        }

        var warnings = result.Warnings;
        // #735: says where the original was kept, so the event feed and the Activity page both carry it (the page reads this same detail).
        var keptNote = result.KeptOriginalPath is { } keptPath ? $" The original was kept at {keptPath}." : string.Empty;
        var detail = $"Cleaned {Path.GetFileName(path)}: {RemovedTracks(plan.RemovedAudioCount, plan.RemovedSubtitleCount)}." +
                     (writerNote is null ? string.Empty : " " + writerNote) +
                     keptNote + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty);
        await RecordAsync(library.Id, path, null, LibraryActivityEventTypes.FileCleaned, detail, "success", result.KeptOriginalPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the library to be scanned again, unless a scan is already waiting to start: it will see every file cleaned so far,
    /// so the cleans of a whole selection ask for one scan between them. A scan already under way may have passed the file, so it does not count.
    /// </summary>
    private async Task RequestScanAsync(UnitOfWork uow, long libraryId)
    {
        var active = await _scans.ActiveScanAsync(uow, libraryId).ConfigureAwait(false);
        if (active is { Status: ProcessingJobStatus.Pending })
        {
            return;
        }

        await _scans.RequestScanAsync(uow, _jobs, libraryId, "worker").ConfigureAwait(false);
    }

    /// <summary>
    /// A locked file is not a failure (#506): back off 5, 15 then 60 minutes, by <see cref="PostponeAsync"/>. After the third
    /// attempt, report it as given up and let the job complete normally.
    /// </summary>
    private async Task OnInUseAsync(JobWorkContext context, WireObject payload, long libraryId, string path, string trigger, int inUseAttempts)
    {
        var attempt = inUseAttempts + 1;
        var delay = SafeSwapRules.InUseRetryDelay(attempt);
        if (delay is null)
        {
            await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileFailed, SafeSwapRules.InUseGaveUpMessage).ConfigureAwait(false);
            return;
        }

        payload.Set("in_use_attempts", attempt);
        await PostponeAsync(context, payload, _time.GetUtcNow() + delay.Value).ConfigureAwait(false);
        _logger.LogInformation("Library clean postponed (in use, attempt {Attempt}) job_id={JobId} path={Path}", attempt, context.Id, path);
    }

    /// <summary>
    /// A drive short of room is not a failure either: the file is looked at again later, spread out the way a file waiting
    /// for room is elsewhere, and never gives up, because room can come back. Each look leaves its reason in Activity.
    /// </summary>
    private async Task OnWaitingForSpaceAsync(JobWorkContext context, WireObject payload, long libraryId, string path, string trigger, string reason)
    {
        var looks = payload.Get("disk_space_looks") is WireInteger counted ? (long)counted.Value : 0;
        payload.Set("disk_space_looks", looks + 1);
        await PostponeAsync(context, payload, _time.GetUtcNow() + DiskSpaceWaits.LookAfter(looks)).ConfigureAwait(false);
        await RecordAsync(libraryId, path, trigger, LibraryActivityEventTypes.FileSkipped, reason).ConfigureAwait(false);
        _logger.LogInformation("Library clean waiting for room, look {Look} job_id={JobId} path={Path}", looks + 1, context.Id, path);
    }

    /// <summary>
    /// Puts the row back to <c>pending</c> with a future <c>not_before</c> and clears the lease ourselves, so
    /// <see cref="ProcessingJobStore.CompleteClaimedAsync"/> finds the lease already gone and leaves this update alone.
    /// </summary>
    private async Task PostponeAsync(JobWorkContext context, WireObject payload, DateTimeOffset notBefore)
    {
        await using var uow = await UnitOfWork.OpenAsync(_database, CancellationToken.None).ConfigureAwait(false);
        await uow.ExecuteAsync(
            "UPDATE jobs SET payload_json = @payload, status = @pending, lease_owner = NULL, lease_expires_at = NULL, not_before = @notBefore, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            ("@payload", WireJsonWriter.Dumps(payload, WireJsonFormat.Compact)),
            ("@pending", ProcessingJobStatus.Pending),
            ("@notBefore", notBefore.UtcDateTime),
            ("@id", context.Id)).ConfigureAwait(false);
        _changes.PublishQueueChangeOnCommit(uow, LibraryModeJobKinds.CleanKind);
        await uow.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The resolution the file was just probed at settles what this clean costs against the resolution budget, whatever the scan
    /// that queued it had cached. Bookkeeping: a failure to write it is logged and never fails the clean.
    /// </summary>
    private async Task RecordMeasuredCostAsync(long jobId, ProbeResult probe, CancellationToken cancellationToken)
    {
        string resolution;
        try
        {
            resolution = RunnerUnits.ResolutionClassForProbe(probe);
        }
        catch (RulesInputException)
        {
            // Track details nobody can read have no resolution to count; the planning step that follows says so in plain words.
            return;
        }

        try
        {
            await LockedWrites.RunAsync(
                _database,
                async uow =>
                {
                    RunnerCosts.RecordMeasured(uow.Connection, await uow.WriteTransactionAsync().ConfigureAwait(false), jobId, resolution);
                },
                _logger,
                "library clean cost",
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            _logger.LogWarning(exception, "Library clean could not record the file's resolution against the budget; job_id={JobId}.", jobId);
        }
    }

    private async Task RecordAsync(long libraryId, string path, string? trigger, string eventType, string detail, string? result = null, string? keptOriginalPath = null)
    {
        try
        {
            await using var uow = await UnitOfWork.OpenAsync(_database, CancellationToken.None).ConfigureAwait(false);
            var extra = new WireObject().Set("library_id", libraryId).Set("relative_media_path", path);
            if (trigger is not null)
            {
                extra.Set("trigger", trigger);
            }

            if (result is not null)
            {
                extra.Set("result", result);
            }

            if (keptOriginalPath is not null)
            {
                extra.Set("kept_original_path", keptOriginalPath);
            }

            await SqliteActivityWriter.RecordAsync(
                    uow,
                    new ActivityEventDraft(eventType, "library", detail, WireJsonWriter.Dumps(extra, WireJsonFormat.Compact)))
                .ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Library mode could not record its activity entry; the outcome remains only in the job row.");
        }
    }

    /// <summary>
    /// The sentence for a file's story when the workflow prefers mkvmerge ("Best") but ffmpeg wrote the file,
    /// because mkvmerge cannot write that container or could not write or validate that file. Null when the
    /// tool the workflow asked for did the writing.
    /// </summary>
    internal static string? FfmpegFallbackNote(string? writerChoice, StagedRemux staged) =>
        RemuxWriterChoice.PrefersBestTool(writerChoice) && staged.Writer is FfmpegRemuxWriter
            ? "Weir wrote it with ffmpeg because mkvmerge could not."
            : null;

    /// <summary>"removed 1 audio track and 2 subtitle tracks", naming only the kinds that lost a track.</summary>
    internal static string RemovedTracks(int audio, int subtitles)
    {
        var parts = new List<string>(2);
        if (audio > 0)
        {
            parts.Add(Plural.Of(audio, "audio track"));
        }

        if (subtitles > 0)
        {
            parts.Add(Plural.Of(subtitles, "subtitle track"));
        }

        return parts.Count == 0 ? "removed no audio or subtitle tracks" : "removed " + string.Join(" and ", parts);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
