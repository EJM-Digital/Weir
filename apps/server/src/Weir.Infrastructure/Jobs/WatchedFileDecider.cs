using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Decides, for one file a watched-folder scan found, what the scan records and whether it queues a pass. The rules are the
/// scan's own; they are worked out against rows read once for the whole scan, with no transaction open (#708).
/// </summary>
internal sealed class WatchedFileDecider
{
    private readonly WatchedFolderScan _scan;
    private readonly WatchedFolderScanLookups _lookups;
    private readonly UnitOfWork _reads;
    private readonly HashSet<string> _queuedThisRun = new(StringComparer.Ordinal);
    private readonly Lazy<string?> _outputFolderProblem;

    /// <param name="scan">What the scan knows before its first file.</param>
    /// <param name="lookups">The rows and passes read once for the whole scan.</param>
    /// <param name="reads">A unit of work the decider only reads through, so it never takes the write lock.</param>
    public WatchedFileDecider(WatchedFolderScan scan, WatchedFolderScanLookups lookups, UnitOfWork reads)
    {
        _scan = scan;
        _lookups = lookups;
        _reads = reads;
        // The output folder's writability is a property of the folder, not of each file, and checking it writes a probe file.
        _outputFolderProblem = new Lazy<string?>(() => WatchedFolderScanOps.OutputFolderProblem(scan.Paths.OutputFolder, scan.Files));
    }

    /// <summary>
    /// A row whose outcome stands and whose file has not changed: it is only marked seen, or a failed file whose retry is due
    /// is queued again. Null when the file needs a closer look.
    /// </summary>
    public WatchedFileDecision? Settled(WatchedMediaFile file, string rel, ProcessingFileRecord? previous)
    {
        if (previous is null)
        {
            return null;
        }

        // The file the last successful pass cleaned (same size and modification time as that pass recorded) is finished, in a
        // library that keeps originals (#627) and for TV, where a season folder can outlive its episodes' passes (#644). A
        // movie whose original was to be removed goes on to the closer look, so an interrupted removal is finished.
        if (ProcessedSourceRules.IsSameCleanedFile(previous, file.SizeBytes, file.ModifiedTimeNs) && (_scan.KeepsOriginals || !_scan.IsMovieScope))
        {
            return Touch(rel, previous);
        }

        if (previous.SizeBytes != file.SizeBytes)
        {
            return null;
        }

        return previous.Status switch
        {
            ProcessingFileStatuses.PassedThrough or ProcessingFileStatuses.Rejected or ProcessingFileStatuses.Cancelled => Touch(rel, previous),
            ProcessingFileStatuses.ProcessingFailed => RetryWhenDue(rel, previous),
            _ => null,
        };
    }

    /// <summary>
    /// A failed file whose retry is owed. Once due, the retry is queued the way a fresh candidate is, not through
    /// RequeueStore: its reset (attempts back to 0) is for a person's "retry now", and here it would let a file that keeps
    /// failing retry for ever. Until then the scan asks for another look at the moment the retry falls due.
    /// </summary>
    private WatchedFileDecision RetryWhenDue(string rel, ProcessingFileRecord row)
    {
        if (row.NextRetryAt?.AsUtc is not { } retryAt)
        {
            return Touch(rel, row);
        }

        return retryAt <= _scan.Now.UtcDateTime
            ? new WatchedFileDecision { RelativePath = rel, Enqueue = true, Previous = row }
            : Touch(rel, row) with { HoldEnds = new DateTimeOffset(retryAt, TimeSpan.Zero) };
    }

    /// <summary>
    /// A person chose "Keep" for this exact file (#785): its watched-folder bytes have not moved on since, so
    /// nothing is recorded and nothing is queued. A same-name replacement fails this check on size or modification
    /// time alone, and is picked up as an ordinary new candidate.
    /// </summary>
    private bool IsKept(string rel, WatchedMediaFile file) =>
        _lookups.SkipMarkers.TryGetValue(rel, out var marker) && marker.SizeBytes == file.SizeBytes && marker.MtimeNs == file.ModifiedTimeNs;

    /// <summary>The full decision for a file whose size and times were just read from the file itself.</summary>
    public async Task<WatchedFileDecision> DecideAsync(WatchedMediaFile file, string rel, ProcessingFileRecord? previous)
    {
        if (IsKept(rel, file))
        {
            return new WatchedFileDecision { RelativePath = rel };
        }

        if (Settled(file, rel, previous) is { } settled)
        {
            return settled;
        }

        // A changed source is a new processing opportunity: its failures do not carry over to the new bytes.
        var resetStatus = previous is not null && previous.SizeBytes != file.SizeBytes
            ? previous.Status is ProcessingFileStatuses.ProcessingFailed or ProcessingFileStatuses.OnHold or ProcessingFileStatuses.PassedThrough
                or ProcessingFileStatuses.Rejected or ProcessingFileStatuses.Cancelled
                ? ProcessingFileStatuses.Unprocessed
                : previous.Status
            : null;
        var current = resetStatus is null ? previous : previous! with { Status = resetStatus, FailureClass = null, FailureAttempts = 0, NextRetryAt = null };
        var settling = FileSettling.ObserveSizeSettling(_scan.Library, previous?.SizeBytes, previous?.SizeChangedAt?.AsUtc, file.SizeBytes, _scan.Now);
        var accessProblem = settling.IsSettling ? null : AccessProblem(file.FullPath);
        var write = new ScannedFileWrite(rel, previous?.Id, previous?.Status, resetStatus, null, file.SizeBytes, settling.SizeChangedAt);
        var cleaned = new WatchedFileDecision { RelativePath = rel, Previous = current, Settling = settling };

        if (ProcessedSourceRules.IsSameCleanedFile(previous, file.SizeBytes, file.ModifiedTimeNs))
        {
            // A cleaned movie in a library that removes originals: its removal was interrupted. Finish it while the validated
            // output is still there. Never a second pass, and the row keeps its outcome. It has the size the pass cleaned, so no
            // reset applies and the row is only marked seen.
            var leaveAsIs = cleaned with { TouchRowId = previous!.Id };
            return !settling.IsSettling && accessProblem is null && !_lookups.ActivePasses.Contains(rel) &&
                   await CompletedOutputAsync(rel, file.FullPath).ConfigureAwait(false)
                ? leaveAsIs with { FinishMovieRemoval = true }
                : leaveAsIs;
        }

        return await DecideUncleanedAsync(file, current, write, settling, accessProblem, cleaned).ConfigureAwait(false);
    }

    private async Task<WatchedFileDecision> DecideUncleanedAsync(
        WatchedMediaFile file, ProcessingFileRecord? current, ScannedFileWrite write, SettlingObservation settling, string? accessProblem, WatchedFileDecision decision)
    {
        var rel = write.RelativePath;
        // The candidate anchor is the file's own stem, so anchor matching only ever compares one release title to another.
        var dispatch = WatchedFileDispatch.Evaluate(
            ManagerQueueSignals.AttributedRowsForFile(_scan.Signals, _scan.MediaScope, file.FullPath),
            new FileAnchorCandidate(Path.GetFileNameWithoutExtension(file.FullPath)));
        var verdict = Verdict(file, rel, current, settling, accessProblem, dispatch.BlockedConnection);
        decision = decision with
        {
            Write = write with { Verdict = verdict },
            HoldEnds = verdict.HoldUntil is { } holdEnds && holdEnds > _scan.Now ? holdEnds : null,
        };

        // Finishing a source removal a lock interrupted: only for a library that removes originals.
        if (_scan.IsMovieScope && !_scan.KeepsOriginals && !settling.IsSettling && accessProblem is null && !_lookups.ActivePasses.Contains(rel) &&
            await CompletedOutputAsync(rel, file.FullPath).ConfigureAwait(false))
        {
            return decision with { FinishMovieRemoval = true };
        }

        if (!verdict.Eligible)
        {
            return decision;
        }

        var facts = new CandidateFileFacts(file.SizeBytes, new DateTimeOffset(file.CreatedUtc, TimeSpan.Zero), new DateTimeOffset(file.ModifiedUtc, TimeSpan.Zero));
        if (LibraryAdmission.Rejection(rel, Path.GetFileName(file.FullPath), facts, _scan.Rules) is { } rejection)
        {
            return Rejected(decision, write, file.FullPath, rejection);
        }

        if (dispatch.Verdict != WatchedFileDispatchOutcome.Proceed || !_scan.EnqueueRemuxJobs || !_queuedThisRun.Add(rel) || _lookups.ActivePasses.Contains(rel))
        {
            return decision;
        }

        // With a fingerprint on record, it alone decides whether this is the cleaned file; the activity-history check (path
        // and size only) would mistake a same-size replacement for it.
        var fingerprintDecides = _scan.KeepsOriginals && ProcessedSourceRules.HasFingerprint(current);
        if (!fingerprintDecides && await CompletedOutputAsync(rel, file.FullPath).ConfigureAwait(false))
        {
            // A movie whose library removes originals never gets here: with its output complete and nothing in the way, it was
            // sent to finish its removal above, and an eligible file has nothing in the way.
            return decision;
        }

        _lookups.ActivePasses.Add(rel);
        return decision with { Enqueue = true };
    }

    private FileStateVerdict Verdict(WatchedMediaFile file, string rel, ProcessingFileRecord? current, SettlingObservation settling, string? accessProblem, string? blockedBy)
    {
        var window = _scan.Window;
        var verdict = FileStateDecision.DecideFileState(
            _scan.Library,
            window.InWindow,
            Math.Max(0.0, (_scan.Now.UtcDateTime - file.ModifiedUtc).TotalSeconds),
            window.PauseReason,
            window.PauseUntil,
            window.ReopensAt,
            settling.IsSettling,
            settling.Reason,
            settling.StableAt,
            accessProblem,
            blockedBy,
            _scan.Now);

        // A pass for this file booked for later is not a file waiting for a free lane; calling it ready would put it first in
        // line on Processing while a lane stood empty. It is on hold until the booked look, for the reason the last pass gave.
        return verdict.Eligible && _lookups.HeldBackPasses.TryGetValue(rel, out var lookAgainAt)
            ? new FileStateVerdict(
                ProcessingFileStatuses.OnHold,
                current is { Status: ProcessingFileStatuses.OnHold } held && held.StatusReason.Length > 0
                    ? held.StatusReason
                    : "Weir has another look at this file booked, and leaves it alone until then.",
                HoldUntil: lookAgainAt)
            : verdict;
    }

    private WatchedFileDecision Rejected(WatchedFileDecision decision, ScannedFileWrite write, string filePath, LibraryAdmissionRejection rejection)
    {
        var reason = rejection.Reason;
        RejectedFileRemoval? removal = null;
        if (_scan.Rules.RejectedFileAction == "delete_file" && !_scan.Links.KeepsOriginals)
        {
            removal = new RejectedFileRemoval(write.RelativePath, filePath, reason, _scan.Rules.RejectedFileAction, rejection.SkipKind);
            reason = $"{reason} This workflow is set to delete rejected files; Weir will record this decision before removing only this file.";
        }

        return decision with { Write = write with { Verdict = new FileStateVerdict(ProcessingFileStatuses.Skipped, reason, SkipKind: rejection.SkipKind) }, Removal = removal };
    }

    private string? AccessProblem(string filePath) =>
        _scan.Library.SkipAccessTests ? null : WatchedFolderScanOps.SourceReadProblem(filePath, _scan.Files) ?? _outputFolderProblem.Value;

    private Task<bool> CompletedOutputAsync(string rel, string sourcePath) =>
        WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync(
            _reads, rel, _scan.MediaScope, _scan.Library.Id, _scan.Paths.OutputFolder, sourcePath, _scan.Files);

    private static WatchedFileDecision Touch(string rel, ProcessingFileRecord row) => new() { RelativePath = rel, TouchRowId = row.Id };
}
