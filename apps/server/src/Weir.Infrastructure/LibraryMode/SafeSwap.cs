using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// The crash-safe in-place swap for library mode (#506): a file in a library is replaced by its cleaned copy so that a crash,
/// power cut, locked file or concurrent change can never lose it or overwrite a newer one.
/// </summary>
/// <remarks>
/// <para>Steps, each of which may fail (the tests inject a failure, and a crash, at every one):</para>
/// <list type="number">
/// <item>Put right any leftovers of an earlier swap of this file (<see cref="SwapRecoverySweep.RecoverFile"/>).</item>
/// <item>Preflight: present, not hardlinked (#508), free space for a copy plus what the workflow keeps free, folder writable, not read-only, not in use; fingerprint.</item>
/// <item>Journal <c>writing</c>; write <c>&lt;name&gt;.weir-tmp&lt;ext&gt;</c> beside the original; validate it.</item>
/// <item>Re-fingerprint the original: changed → discard the copy, "The file changed while Weir was working; nothing was replaced".</item>
/// <item>Copy the original's permissions to the copy (best effort; never the mtime).</item>
/// <item>Journal <c>committing</c>; rename original → <c>&lt;name&gt;.weir-bak&lt;ext&gt;</c>; check the backup is still the fingerprinted file.</item>
/// <item>Rename copy → original name. <b>This rename is the commit.</b></item>
/// <item>
/// Journal <c>committed</c>; delete the backup, or — when the library's "keep the original after clean" setting (#735) is
/// on — move it into its originals folder instead (a failure is logged, the sweep retries either way); journal <c>finished</c>.
/// </item>
/// </list>
/// <para>
/// Any failure before the commit rolls back by looking at the files, not at how far the code got: if the original name is
/// empty and the backup exists the backup is renamed back, and the temp file is deleted. A crash skips the rollback, and
/// <see cref="SwapRecoverySweep"/> applies the same rules at the next start. Either way exactly one intact file is left under the
/// original name: the original content before the commit rename, the cleaned content after it.
/// </para>
/// <para>
/// A file held by another program (a Windows sharing violation, <c>EBUSY</c>) is not a failure: the result is
/// <see cref="SwapOutcome.InUse"/> and the caller requeues it with <see cref="SafeSwapRules.InUseRetryDelay"/>.
/// </para>
/// </remarks>
public sealed partial class SafeSwap
{
    private readonly ISwapFileSystem _files;
    private readonly ISwapJournal _journal;
    private readonly ISwapOutputValidator _validator;
    private readonly ILogger _logger;
    private readonly IOutputOwnership? _ownership;
    private readonly ISwapProgress? _progress;

    public SafeSwap(
        ISwapFileSystem files,
        ISwapJournal journal,
        ISwapOutputValidator validator,
        ILogger<SafeSwap> logger,
        IOutputOwnership? ownership = null,
        ISwapProgress? progress = null)
    {
        _files = files;
        _journal = journal;
        _validator = validator;
        _logger = logger;
        _ownership = ownership;
        _progress = progress;
    }

    /// <summary>The checks made before any work, without changing anything. A scan can show the refusal reasons.</summary>
    public SwapPreflight Preflight(string originalPath, SwapOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        options ??= SwapOptions.Default;
        var notes = new List<string>();
        SwapPreflight Refuse(SwapOutcome outcome, string message, SourceFingerprint fingerprint = default) =>
            new(originalPath, outcome, message, fingerprint, notes);

        try
        {
            if (!_files.FileExists(originalPath))
            {
                return Refuse(SwapOutcome.SourceMissing, SafeSwapRules.SourceMissingMessage);
            }

            var fingerprint = _files.Fingerprint(originalPath);
            var links = _files.LinkCount(originalPath);
            if (links is null)
            {
                notes.Add("Weir could not check whether this file is hardlinked on this platform.");
            }
            else if (links > 1 && !options.AllowHardlinked)
            {
                return Refuse(SwapOutcome.Hardlinked, SafeSwapRules.HardlinkedMessage, fingerprint);
            }

            var directory = DirectoryOf(originalPath);
            var required = SafeSwapRules.RequiredFreeBytes(fingerprint.SizeBytes, options.KeepFreeBytes);
            var available = _files.AvailableFreeBytes(directory);
            if (available is null)
            {
                notes.Add("Weir could not read the free space on this file's volume.");
            }
            else if (available < required)
            {
                return Refuse(SwapOutcome.InsufficientSpace, SafeSwapRules.InsufficientSpaceMessage(required, available.Value, options.KeepFreeBytes), fingerprint);
            }

            try
            {
                _files.ProbeWrite(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse(SwapOutcome.NotWritable, SafeSwapRules.NotWritableMessage(exception.Message), fingerprint);
            }

            if (_files.IsReadOnly(originalPath))
            {
                return Refuse(SwapOutcome.NotWritable, SafeSwapRules.ReadOnlyMessage, fingerprint);
            }

            if (_files.IsInUse(originalPath))
            {
                return Refuse(SwapOutcome.InUse, SafeSwapRules.InUseMessage, fingerprint);
            }

            return new SwapPreflight(originalPath, null, null, fingerprint, notes);
        }
        catch (FileNotFoundException)
        {
            return Refuse(SwapOutcome.SourceMissing, SafeSwapRules.SourceMissingMessage);
        }
        catch (FileInUseException)
        {
            return Refuse(SwapOutcome.InUse, SafeSwapRules.InUseMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse(SwapOutcome.Failed, SafeSwapRules.FailedMessage("checking the file before starting", exception.Message));
        }
    }

    /// <summary>
    /// Preflight, write the cleaned copy with <paramref name="writeOutput"/>, validate it and swap it in, recording progress on
    /// job <paramref name="jobId"/>. Never throws for a filesystem or journal failure; cancellation before the commit rolls back
    /// and rethrows, and is not observed once the commit has started.
    /// </summary>
    public async Task<SwapResult> RunAsync(
        long jobId,
        string originalPath,
        SwapOutputWriter writeOutput,
        SwapOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        ArgumentNullException.ThrowIfNull(writeOutput);
        var temp = SafeSwapRules.TempPath(originalPath);
        var backup = SafeSwapRules.BackupPath(originalPath);
        var warnings = new List<string>();

        // An earlier swap of this file that stopped half-way: finish putting it right first, so a stale backup can never be
        // mistaken for this swap's (and restored over a newer file).
        SwapRecoverySweep.RecoverFile(_files, originalPath, _logger);
        if (Exists(temp) is not false || Exists(backup) is not false)
        {
            return new SwapResult(
                SwapOutcome.Failed,
                SafeSwapRules.FailedMessage("clearing up an earlier interrupted swap", "its leftover files could not be removed"),
                false,
                warnings);
        }

        var preflight = Preflight(originalPath, options);
        if (!preflight.Ready)
        {
            return new SwapResult(preflight.Refusal!.Value, preflight.Message!, false, warnings);
        }

        warnings.AddRange(preflight.Notes);
        var stage = "recording that the swap started";
        string? keepDestination = null;
        try
        {
            await _journal.RecordAsync(new SwapJournalEntry(jobId, originalPath, SwapJournalState.Writing), cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // No swap starts without its journal entry; any failure to write it stops here.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap not started: the journal could not be written job_id={JobId} path={Path}", jobId, originalPath);
            return new SwapResult(SwapOutcome.Failed, SafeSwapRules.FailedMessage(stage, exception.Message), false, warnings);
        }

        _progress?.Reached(SwapPoint.Started);

        async Task<SwapResult> Abandon(SwapOutcome outcome, string message)
        {
            await RollbackAsync(jobId, originalPath).ConfigureAwait(false);
            // #735: a reservation made before the abandon point is never filled now; clean it up rather than leave an
            // empty stray file in the originals folder. Best-effort — it never affects the rollback outcome above.
            if (keepDestination is not null)
            {
                OriginalsMover.CleanUpUnfilledReservation(_files, keepDestination);
            }

            return new SwapResult(outcome, message, false, warnings);
        }

        try
        {
            stage = "writing the cleaned copy";
            await writeOutput(temp, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_files.FileExists(temp))
            {
                return await Abandon(SwapOutcome.Failed, SafeSwapRules.FailedMessage(stage, "no cleaned copy was written"));
            }

            stage = "checking the cleaned copy";
            var validation = await _validator.ValidateAsync(originalPath, temp, options?.OriginalDurationSeconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!validation.Passed)
            {
                return await Abandon(SwapOutcome.ValidationFailed, SafeSwapRules.ValidationFailedMessage(validation.Problem));
            }

            stage = "checking the original again";
            if (!_files.FileExists(originalPath) || !SameFile(_files.Fingerprint(originalPath), preflight.Fingerprint))
            {
                _logger.LogWarning("Library swap abandoned: the original changed while Weir worked job_id={JobId} path={Path}", jobId, originalPath);
                return await Abandon(SwapOutcome.SourceChanged, SafeSwapRules.SourceChangedMessage);
            }

            _progress?.Reached(SwapPoint.CopyChecked);

            // From here the swap takes milliseconds and is not cancelled part-way.
            stage = "copying the file's permissions";
            try
            {
                _files.CopyPermissions(originalPath, temp);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
            {
                _logger.LogWarning(exception, "Library swap could not copy the file's permissions job_id={JobId} path={Path}", jobId, originalPath);
                warnings.Add($"Weir could not give the cleaned file the original's permissions: {exception.Message}");
            }

            // #735: decided and journalled before any commit file mutation, so a crash after this point always
            // finds the same destination in the journal rather than losing the setting's intent.
            keepDestination = ResolveKeepDestination(options?.KeepOriginal, originalPath);
            stage = "recording that the swap is committing";
            await _journal.RecordAsync(new SwapJournalEntry(jobId, originalPath, SwapJournalState.Committing, keepDestination), CancellationToken.None).ConfigureAwait(false);
            _progress?.Reached(SwapPoint.CommitRecorded);

            stage = "moving the original aside";
            _files.Move(originalPath, backup);

            stage = "checking the original again";
            if (!SameFile(_files.Fingerprint(backup), preflight.Fingerprint))
            {
                _logger.LogWarning("Library swap abandoned: the original changed as Weir moved it aside job_id={JobId} path={Path}", jobId, originalPath);
                return await Abandon(SwapOutcome.SourceChanged, SafeSwapRules.SourceChangedMessage);
            }

            _progress?.Reached(SwapPoint.OriginalMovedAside);

            stage = "putting the cleaned copy in place";
            _files.Move(temp, originalPath);
            // #555: the swapped-in file is a fresh name (the rename above), so it needs the output-ownership
            // policy applied like any other file Weir just published; never fails the swap.
            _ownership?.ApplyToFile(originalPath);
            _progress?.Reached(SwapPoint.CleanedCopyInPlace);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(jobId, originalPath).ConfigureAwait(false);
            throw;
        }
        catch (FileInUseException exception)
        {
            _logger.LogInformation("Library swap postponed: the file is in use job_id={JobId} path={Path} stage={Stage} detail={Detail}", jobId, originalPath, stage, exception.Message);
            return await Abandon(SwapOutcome.InUse, SafeSwapRules.InUseMessage);
        }
#pragma warning disable CA1031 // Any failure before the commit rolls back to the original file.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap rolled back job_id={JobId} path={Path} stage={Stage}", jobId, originalPath, stage);
            return await Abandon(SwapOutcome.Failed, SafeSwapRules.FailedMessage(stage, exception.Message));
        }

        // Committed: the cleaned copy has the original's name. Nothing below may undo that.
        _logger.LogInformation("Library swap committed job_id={JobId} path={Path}", jobId, originalPath);
        try
        {
            await _journal.RecordAsync(new SwapJournalEntry(jobId, originalPath, SwapJournalState.Committed, keepDestination), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The swap is committed; nothing below may undo it, so a failed record is only a warning.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap committed but not recorded job_id={JobId} path={Path}", jobId, originalPath);
            warnings.Add($"Weir replaced the file but could not record it on the job: {exception.Message}");
        }

        _progress?.Reached(SwapPoint.CommittedRecorded);

        var backupRemoved = false;
        string? keptOriginalPath = null;
        try
        {
            if (keepDestination is not null)
            {
                await BlockingWork.RunAsync(() => OriginalsMover.Fill(_files, backup, keepDestination)).ConfigureAwait(false);
                keptOriginalPath = keepDestination;
            }
            else
            {
                _files.Delete(backup);
            }

            backupRemoved = true;
        }
#pragma warning disable CA1031 // A backup that cannot be dealt with is left for the startup sweep; the swap stands.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (keepDestination is not null)
            {
                _logger.LogWarning(exception, "Library swap left its original for the startup sweep to move job_id={JobId} backup={Backup} destination={Destination}", jobId, backup, keepDestination);
                warnings.Add(SafeSwapRules.OriginalKeepFailedMessage(keepDestination, exception.Message));
            }
            else
            {
                _logger.LogWarning(exception, "Library swap left its backup for the startup sweep job_id={JobId} backup={Backup}", jobId, backup);
                warnings.Add($"Weir replaced the file but could not delete the backup copy ({backup}); it will try again when it next starts.");
            }
        }

        if (backupRemoved)
        {
            _progress?.Reached(SwapPoint.BackupDealtWith);
            try
            {
                await _journal.RecordAsync(new SwapJournalEntry(jobId, originalPath, SwapJournalState.Finished), CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The swap is finished; a failed record is logged, never undoes it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Library swap finished but not recorded job_id={JobId} path={Path}", jobId, originalPath);
            }

            _progress?.Reached(SwapPoint.FinishedRecorded);
        }

        return new SwapResult(SwapOutcome.Committed, SafeSwapRules.CommittedMessage, backupRemoved, warnings, keptOriginalPath);
    }

}
