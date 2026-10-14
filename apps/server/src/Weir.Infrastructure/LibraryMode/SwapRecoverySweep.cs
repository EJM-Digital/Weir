using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.LibraryMode;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>What recovery did.</summary>
/// <param name="TempFilesDeleted">Leftover <c>.weir-tmp</c> copies removed.</param>
/// <param name="BackupsDeleted">Leftover <c>.weir-bak</c> files removed once the commit they belonged to was confirmed.</param>
/// <param name="BackupsRestored">Backups renamed back because their commit never happened.</param>
/// <param name="Problems">Leftovers that could not be dealt with, logged and left for the next sweep.</param>
/// <param name="KeepConflicts">#735: original-file paths whose kept original could not be reconciled with its backup
/// (<see cref="OriginalsKeepConflictException"/>) — a subset of what made up <paramref name="Problems"/>, for the caller
/// to record an Activity event about and stop retrying, rather than leaving the swap "unfinished" forever.</param>
public sealed record SwapRecoveryReport(int TempFilesDeleted, int BackupsDeleted, int BackupsRestored, int Problems, IReadOnlyList<string>? KeepConflicts = null)
{
    /// <summary>Defaults to empty rather than null, for every construction site written before #735.</summary>
    public IReadOnlyList<string> KeepConflicts { get; init; } = KeepConflicts ?? [];

    public static SwapRecoveryReport Empty { get; } = new(0, 0, 0, 0);

    public int FilesChanged => TempFilesDeleted + BackupsDeleted + BackupsRestored;

    /// <summary>
    /// Sequence, not reference, equality for <see cref="KeepConflicts"/>: two collection-expression literals, or the
    /// same empty list built two different ways, are never the same instance, but every existing test written before
    /// #735 (and its own empty-list default) still needs <c>Assert.Equal(new SwapRecoveryReport(...), report)</c> to work.
    /// </summary>
    public bool Equals(SwapRecoveryReport? other) =>
        other is not null &&
        TempFilesDeleted == other.TempFilesDeleted &&
        BackupsDeleted == other.BackupsDeleted &&
        BackupsRestored == other.BackupsRestored &&
        Problems == other.Problems &&
        KeepConflicts.SequenceEqual(other.KeepConflicts, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(TempFilesDeleted, BackupsDeleted, BackupsRestored, Problems, KeepConflicts.Count);

    public static SwapRecoveryReport operator +(SwapRecoveryReport left, SwapRecoveryReport right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(
            left.TempFilesDeleted + right.TempFilesDeleted,
            left.BackupsDeleted + right.BackupsDeleted,
            left.BackupsRestored + right.BackupsRestored,
            left.Problems + right.Problems,
            [.. left.KeepConflicts, .. right.KeepConflicts]);
    }

    public static SwapRecoveryReport Add(SwapRecoveryReport left, SwapRecoveryReport right) => left + right;
}

/// <summary>
/// The startup sweep for interrupted library swaps (#506). Run it before any worker starts.
/// The approach follows Muxarr's <c>CleanupMuxbakFiles</c>/<c>RestoreFromBackup</c> (https://github.com/KirovAir/muxarr, GPL-3.0);
/// see THIRD_PARTY_NOTICES.md.
/// </summary>
/// <remarks>
/// <para>For each original it looks at the files only, so it is correct whatever step a crash interrupted:</para>
/// <list type="bullet">
/// <item><c>&lt;name&gt;.weir-bak&lt;ext&gt;</c> and the original present → the commit happened (or a newer file arrived): delete the backup.</item>
/// <item><c>&lt;name&gt;.weir-bak&lt;ext&gt;</c> and the original missing → the commit did not happen: rename the backup back, with a warning.</item>
/// <item><c>&lt;name&gt;.weir-tmp&lt;ext&gt;</c> → delete it (a cleaned copy never put in place; the job runs again).</item>
/// <item>An empty kept-original reservation the journal names, with the original in place and the cleaned copy unused → release it.</item>
/// </list>
/// <para>
/// The paths recorded on unfinished job rows (<see cref="ISwapJournal"/>) are handled first; walking the library folders is only a
/// fallback, for a journal that could not be written or a job row already pruned.
/// </para>
/// </remarks>
public sealed class SwapRecoverySweep
{
    private readonly ISwapFileSystem _files;
    private readonly ISwapJournal _journal;
    private readonly IActivityWriter _activityWriter;
    private readonly ILogger _logger;

    public SwapRecoverySweep(ISwapFileSystem files, ISwapJournal journal, IActivityWriter activityWriter, ILogger<SwapRecoverySweep> logger)
    {
        _files = files;
        _journal = journal;
        _activityWriter = activityWriter;
        _logger = logger;
    }

    /// <param name="libraryFolders">Library folders to walk when <paramref name="walkFolders"/> is set.</param>
    /// <param name="walkFolders">Also look for leftovers the journal does not know about.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    public async Task<SwapRecoveryReport> RunAsync(
        IEnumerable<string> libraryFolders,
        bool walkFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        var report = SwapRecoveryReport.Empty;
        var handled = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        IReadOnlyList<SwapJournalEntry> unfinished;
        try
        {
            unfinished = await _journal.ListUnfinishedAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Without the job records the sweep still walks the folders.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap sweep could not read the swaps recorded on job rows; only a folder walk can find leftovers");
            unfinished = [];
            report += new SwapRecoveryReport(0, 0, 0, 1);
        }

        IReadOnlyList<string> waitingForAPerson;
        try
        {
            waitingForAPerson = await _journal.ListKeepConflictPathsAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Without knowing which backups wait for a person, no folder walk may delete one.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap sweep could not read which kept originals need a person; it will not walk the library folders");
            waitingForAPerson = [];
            walkFolders = false;
            report += new SwapRecoveryReport(0, 0, 0, 1);
        }

        handled.UnionWith(waitingForAPerson);
        foreach (var entry in unfinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileReport = RecoverFile(_files, entry.OriginalPath, _logger, keptOriginalPath: entry.KeptOriginalPath);
            report += fileReport;
            handled.Add(entry.OriginalPath);

            if (fileReport.KeepConflicts.Count > 0)
            {
                // #735 review, MINOR 4: a conflict is not transient like a lock or a permissions error, so it is not
                // worth retrying every sweep. Recorded once (an Activity event, then a terminal journal state) and
                // left for a person, rather than warned about silently on every future start.
                await RecordKeepConflictAsync(entry, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (fileReport.Problems > 0)
            {
                continue;
            }

            try
            {
                await _journal.RecordAsync(entry with { State = SwapJournalState.Recovered }, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A recovered file stays recovered even when the record of it cannot be written.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Library swap sweep recovered a file but could not record it job_id={JobId} path={Path}", entry.JobId, entry.OriginalPath);
            }
        }

        if (!walkFolders)
        {
            return Log(report);
        }

        foreach (var folder in libraryFolders.Where(folder => !string.IsNullOrWhiteSpace(folder)))
        {
            List<string> leftovers;
            try
            {
                leftovers = _files.EnumerateLeftovers(folder).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(exception, "Library swap sweep could not walk folder={Folder}", folder);
                report += new SwapRecoveryReport(0, 0, 0, 1);
                continue;
            }

            foreach (var leftover in leftovers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SafeSwapRules.TryParseLeftover(leftover, out _, out var original) && handled.Add(original))
                {
                    report += RecoverFile(_files, original, _logger);
                }
            }
        }

        return Log(report);
    }

    /// <summary>Put one original's leftovers right (see the class remarks). Never throws; failures are counted and logged.</summary>
    /// <param name="files">The filesystem.</param>
    /// <param name="originalPath">The library file whose leftovers are checked.</param>
    /// <param name="logger">Where each action is logged.</param>
    /// <param name="restoreQuietly">A rollback restoring its own backup: expected, so not a warning.</param>
    /// <param name="keptOriginalPath">#735: where the journal says a kept original belongs, when the setting was on for this
    /// swap; null moves straight to deleting the backup, as when the setting is off.</param>
    public static SwapRecoveryReport RecoverFile(
        ISwapFileSystem files, string originalPath, ILogger logger, bool restoreQuietly = false, string? keptOriginalPath = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);
        var backup = SafeSwapRules.BackupPath(originalPath);
        var temp = SafeSwapRules.TempPath(originalPath);
        int tempsDeleted = 0, backupsDeleted = 0, backupsRestored = 0, problems = 0;
        List<string>? keepConflicts = null;

        try
        {
            if (files.FileExists(backup))
            {
                if (files.FileExists(originalPath))
                {
                    if (keptOriginalPath is not null)
                    {
                        OriginalsMover.Recover(files, backup, keptOriginalPath);
                    }
                    else
                    {
                        files.Delete(backup);
                    }

                    backupsDeleted++;
                    if (keptOriginalPath is not null)
                    {
                        logger.LogInformation(
                            "Library swap original moved to its kept location; the file under the original name is kept backup={Backup} kept={Destination}",
                            backup,
                            keptOriginalPath);
                    }
                    else
                    {
                        logger.LogInformation("Library swap backup removed; the file under the original name is kept backup={Backup}", backup);
                    }
                }
                else
                {
                    files.Move(backup, originalPath);
                    backupsRestored++;
                    // The commit never happened, so a kept-original reservation (if the crash landed after it was
                    // made) was never filled either; clean it up now the original is back where it belongs.
                    if (keptOriginalPath is not null)
                    {
                        OriginalsMover.CleanUpUnfilledReservation(files, keptOriginalPath);
                    }

                    if (restoreQuietly)
                    {
                        logger.LogInformation("Library swap put the original back path={Path}", originalPath);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Weir restored a library file from its backup: a swap was interrupted before the cleaned copy was in place path={Path}",
                            originalPath);
                    }
                }
            }
            else if (keptOriginalPath is not null && files.FileExists(temp) && files.FileExists(originalPath))
            {
                // The swap stopped after it reserved the place for the kept original and before it moved the original aside:
                // the cleaned copy was never used, so nothing was ever put in the reservation.
                OriginalsMover.CleanUpUnfilledReservation(files, keptOriginalPath);
            }
        }
        catch (OriginalsKeepConflictException exception)
        {
            problems++;
            keepConflicts = [originalPath];
            logger.LogWarning(exception, "Library swap recovery found a kept original that does not match its backup backup={Backup}", backup);
        }
#pragma warning disable CA1031 // One leftover that cannot be dealt with is counted and logged; the sweep carries on.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            problems++;
            logger.LogWarning(exception, "Library swap recovery could not deal with the backup backup={Backup}", backup);
        }

        try
        {
            if (files.FileExists(temp))
            {
                files.Delete(temp);
                tempsDeleted++;
            }
        }
#pragma warning disable CA1031 // One leftover that cannot be dealt with is counted and logged; the sweep carries on.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            problems++;
            logger.LogWarning(exception, "Library swap recovery could not delete the temp file temp={Temp}", temp);
        }

        return new SwapRecoveryReport(tempsDeleted, backupsDeleted, backupsRestored, problems, keepConflicts);
    }

    /// <summary>
    /// #735 review, MINOR 4: tells the operator once that a kept original needs a human, and marks the journal entry
    /// <see cref="SwapJournalState.KeepConflict"/> so it is never "unfinished" again — the sweep will not retry it, or
    /// alert about it a second time.
    /// </summary>
    private async Task RecordKeepConflictAsync(SwapJournalEntry entry, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(entry.OriginalPath);
        var detail = $"Weir kept both copies of {fileName}; check {entry.KeptOriginalPath} and remove the incomplete one.";
        try
        {
            var extra = new WireObject().Set("relative_path", entry.OriginalPath).Set("kept_original_path", entry.KeptOriginalPath);
            await _activityWriter.RecordAsync(
                    new ActivityEventDraft(LibraryActivityEventTypes.OriginalKeepConflict, "library", detail, WireJsonWriter.Dumps(extra, WireJsonFormat.Compact)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort: a failed Activity write must not stop the journal from being marked, or spam every future sweep.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap sweep found a kept-original conflict but could not record it job_id={JobId} path={Path}", entry.JobId, entry.OriginalPath);
        }

        try
        {
            await _journal.RecordAsync(entry with { State = SwapJournalState.KeepConflict }, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The conflict is logged and (best-effort) recorded above either way; a failed journal write only risks one more sweep re-attempting it.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap sweep could not mark a kept-original conflict on the journal job_id={JobId} path={Path}", entry.JobId, entry.OriginalPath);
        }
    }

    private SwapRecoveryReport Log(SwapRecoveryReport report)
    {
        if (report.FilesChanged > 0 || report.Problems > 0)
        {
            _logger.LogWarning(
                "Library swap sweep finished temp_files_deleted={TempFilesDeleted} backups_deleted={BackupsDeleted} backups_restored={BackupsRestored} problems={Problems}",
                report.TempFilesDeleted,
                report.BackupsDeleted,
                report.BackupsRestored,
                report.Problems);
        }

        return report;
    }
}
