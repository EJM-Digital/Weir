using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Finishes removing a cleaned movie's original download after an earlier removal was interrupted (a lock, a stop), for a
/// library that removes originals. The file keeps its processed outcome whatever happens.
/// </summary>
internal static class CompletedMovieRemoval
{
    /// <summary>
    /// Removes the file (and its release folder when nothing else in it is a video), then records what happened. The removal
    /// runs with no transaction open; the rows are written afterwards in one short one.
    /// </summary>
    public static async Task FinishAsync(
        SqliteDatabase database, FileStateStore files, WatchedFolderScan scan, WatchedFileDecision decision, WatchedMediaFile file, CancellationToken cancellationToken)
    {
        var library = scan.Library;
        var (removed, folderRemoved, reason) = WatchedFolderScanOps.RetryCompletedMovieSourceCleanup(scan.Paths.WatchedFolder, file.FullPath, library.MediaExtensionsCsv, scan.Links);
        var uow = await UnitOfWork.OpenAsync(database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            if (decision.Write is { ResetStatus: not null } reset)
            {
                await files.RecordScannedStateAsync(uow, library.Id, reset with { Verdict = null }, scan.Now).ConfigureAwait(false);
            }

            var verdict = !removed
                // The file is cleaned; only removing its original is waiting. It stays processed, so the fingerprint keeps
                // recognising it and no scan cleans it again once the manager has imported the output (#644). It is not "on
                // hold": a held file looks unfinished everywhere.
                ? new FileStateVerdict(
                    ProcessingFileStatuses.Processed,
                    $"The output is complete, but removing the original download is waiting: {reason} Weir will try again automatically.")
                : folderRemoved
                    ? new FileStateVerdict(ProcessingFileStatuses.Processed, "The output was already complete. The temporary lock cleared, so Weir finished removing the source release folder.")
                    // The folder also holds other videos: only this file went, and the other files keep their own state.
                    : new FileStateVerdict(ProcessingFileStatuses.Processed, $"The output was already complete. {reason}");
            if (removed && folderRemoved)
            {
                await MarkReleaseFolderProcessedAsync(uow, library.Id, decision.RelativePath).ConfigureAwait(false);
            }

            await files.RecordFileStateAsync(
                uow, library.Id, decision.RelativePath, verdict, file.SizeBytes, decision.Settling!.SizeChangedAt, scan.Now).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every other file Weir listed in the removed release folder went with it.</summary>
    internal static async Task MarkReleaseFolderProcessedAsync(UnitOfWork uow, long libraryId, string rel)
    {
        var releaseParent = PosixParent(rel);
        var rows = await uow.QueryAsync(
            "SELECT id, relative_path FROM files WHERE library_id = @lib",
            reader => (Id: reader.GetInt64(0), RelativePath: reader.GetString(1)),
            ("@lib", libraryId)).ConfigureAwait(false);
        foreach (var (id, _) in rows.Where(row => PosixParent(row.RelativePath) == releaseParent))
        {
            await uow.ExecuteAsync(
                "UPDATE files SET status = @status, status_reason = @reason, blocked_by_connection = NULL, hold_until = NULL WHERE id = @id",
                ("@status", ProcessingFileStatuses.Processed),
                ("@reason", "Weir removed this file with its release folder after the validated movie output completed. No separate output was created for this extra file."),
                ("@id", id)).ConfigureAwait(false);
        }

        // No Activity row records this, so the lists that show these files are told once the rows commit.
        ActivityNotifications.TrackFileListChange(uow);
    }

    private static string PosixParent(string relativePosix)
    {
        var index = relativePosix.LastIndexOf('/');
        return index < 0 ? string.Empty : relativePosix[..index];
    }
}
