using Microsoft.Extensions.Logging;
using Weir.Core.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Artwork;

/// <summary>
/// Keeps poster data from growing for ever. A file Weir no longer knows is remembered for <see cref="ArtworkSchedule.GoneFileGrace"/>
/// (so a download that comes back does not fetch its poster again); after that its row goes, then any title nothing uses, then any
/// poster no title uses, image and all. A title is left the same grace from when it was queued, so one asked about for a file Weir
/// has not titled yet is not asked about again. What stays is one small row per title and per file Weir still knows.
/// </summary>
public sealed class ArtworkPruner
{
    private const string FileIsKnown =
        "(EXISTS (SELECT 1 FROM files f WHERE f.library_id = artwork_files.library_id AND f.relative_path = artwork_files.relative_path) " +
        "OR EXISTS (SELECT 1 FROM library_files lf WHERE lf.library_id = artwork_files.library_id AND lf.path = artwork_files.relative_path))";

    private readonly SqliteDatabase _database;
    private readonly ArtworkPosterFiles _posterFiles;
    private readonly TimeProvider _time;
    private readonly ILogger<ArtworkPruner> _logger;

    public ArtworkPruner(SqliteDatabase database, ArtworkPosterFiles posterFiles, TimeProvider time, ILogger<ArtworkPruner> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(posterFiles);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _posterFiles = posterFiles;
        _time = time;
        _logger = logger;
    }

    /// <summary>One pruning pass. Returns how many poster images it removed.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        List<string> removedPosters;
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            await MarkAndUnmarkGoneFilesAsync(uow, now).ConfigureAwait(false);
            await uow.ExecuteAsync(
                "DELETE FROM artwork_files WHERE orphaned_at IS NOT NULL AND julianday(orphaned_at) <= julianday($cutoff)",
                ("$cutoff", TimestampColumns.Orm(now - ArtworkSchedule.GoneFileGrace))).ConfigureAwait(false);
            await uow.ExecuteAsync(
                "DELETE FROM artwork_lookups WHERE julianday(created_at) <= julianday($cutoff) " +
                "AND NOT EXISTS (SELECT 1 FROM artwork_files WHERE artwork_files.lookup_key = artwork_lookups.lookup_key)",
                ("$cutoff", TimestampColumns.Orm(now - ArtworkSchedule.GoneFileGrace))).ConfigureAwait(false);
            removedPosters = await uow.QueryAsync(
                "SELECT poster_id FROM artwork_posters WHERE NOT EXISTS (SELECT 1 FROM artwork_lookups WHERE artwork_lookups.poster_id = artwork_posters.poster_id)",
                reader => SqliteValues.GetString(reader, 0)).ConfigureAwait(false);
            foreach (var posterId in removedPosters)
            {
                await uow.ExecuteAsync("DELETE FROM artwork_posters WHERE poster_id = $id", ("$id", posterId)).ConfigureAwait(false);
            }

            await uow.CommitAsync().ConfigureAwait(false);
        }

        foreach (var posterId in removedPosters)
        {
            DeleteImage(posterId);
        }

        return removedPosters.Count;
    }

    private static async Task MarkAndUnmarkGoneFilesAsync(UnitOfWork uow, DateTimeOffset now)
    {
        await uow.ExecuteAsync(
            $"UPDATE artwork_files SET orphaned_at = $now WHERE orphaned_at IS NULL AND NOT {FileIsKnown}",
            ("$now", TimestampColumns.Orm(now))).ConfigureAwait(false);
        await uow.ExecuteAsync($"UPDATE artwork_files SET orphaned_at = NULL WHERE orphaned_at IS NOT NULL AND {FileIsKnown}").ConfigureAwait(false);
    }

    private void DeleteImage(string posterId)
    {
        try
        {
            _posterFiles.Delete(posterId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Weir could not remove a poster image it no longer needs.");
        }
    }
}
