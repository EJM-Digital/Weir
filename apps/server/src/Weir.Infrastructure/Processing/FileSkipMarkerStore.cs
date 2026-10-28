using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>One file a person chose to keep without processing again (#785), by the exact bytes Weir saw it at.</summary>
public sealed record FileSkipMarker(long SizeBytes, long MtimeNs);

/// <summary>One row of the "Kept files" list: a marker with enough to show and act on it (#786 review of #785).</summary>
public sealed record KeptFileRow(long Id, long LibraryId, string LibraryName, string RelativePath, long SizeBytes, long MtimeNs, Timestamp CreatedAt);

/// <summary>
/// SQLite access for <c>file_skip_markers</c>: a watched-folder scan reads every marker for a library once
/// (<see cref="ForLibraryAsync"/>) and skips a candidate whose current size and modification time still match its
/// marker. A person choosing "Keep" writes one (<see cref="SetAsync"/>); choosing "delete" or "retry" instead clears
/// it (<see cref="ClearAsync"/>), so a later "Keep" of a changed file is not shadowed by a stale row. The "Kept
/// files" list (<see cref="ListAsync"/>) is how a marker is ever seen again, and its own "Process again"
/// (<see cref="ClearByIdAsync"/>) is how one is undone by hand.
/// </summary>
public sealed class FileSkipMarkerStore
{
    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once a write commits, that the kept files changed, so every open screen reads them again.</param>
    public FileSkipMarkerStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public async Task<Dictionary<string, FileSkipMarker>> ForLibraryAsync(UnitOfWork uow, long libraryId)
    {
        var rows = await uow.QueryAsync(
            "SELECT relative_path, size_bytes, mtime_ns FROM file_skip_markers WHERE library_id = @lib",
            reader => (Path: reader.GetString(0), Marker: new FileSkipMarker(reader.GetInt64(1), reader.GetInt64(2))),
            ("@lib", libraryId)).ConfigureAwait(false);
        return rows.ToDictionary(row => row.Path, row => row.Marker, StringComparer.Ordinal);
    }

    /// <summary>Every kept file, newest first, with the library name a person recognises rather than its id alone.</summary>
    public Task<List<KeptFileRow>> ListAsync(UnitOfWork uow) =>
        uow.QueryAsync(
            "SELECT m.id, m.library_id, COALESCE(l.name, 'Unknown library'), m.relative_path, m.size_bytes, m.mtime_ns, m.created_at " +
            "FROM file_skip_markers m LEFT JOIN libraries l ON l.id = m.library_id ORDER BY m.created_at DESC",
            reader => new KeptFileRow(
                reader.GetInt64(0), reader.GetInt64(1), SqliteValues.GetString(reader, 2), SqliteValues.GetString(reader, 3),
                reader.GetInt64(4), reader.GetInt64(5), SqliteValues.GetDateTime(reader, 6)));

    /// <summary>The library and path one marker (by its own row id) names, or null once it is gone.</summary>
    public async Task<(long LibraryId, string RelativePath)?> FindByIdAsync(UnitOfWork uow, long id)
    {
        var rows = await uow.QueryAsync(
            "SELECT library_id, relative_path FROM file_skip_markers WHERE id = @id",
            reader => (LibraryId: reader.GetInt64(0), RelativePath: SqliteValues.GetString(reader, 1)),
            ("@id", id)).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task SetAsync(UnitOfWork uow, long libraryId, string relativePath, long sizeBytes, long mtimeNs)
    {
        await uow.ExecuteAsync(
            "INSERT INTO file_skip_markers (library_id, relative_path, size_bytes, mtime_ns) VALUES (@lib, @path, @size, @mtime) " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET size_bytes = @size, mtime_ns = @mtime, created_at = CURRENT_TIMESTAMP",
            ("@lib", libraryId), ("@path", relativePath), ("@size", sizeBytes), ("@mtime", mtimeNs)).ConfigureAwait(false);
        Changed(uow);
    }

    public async Task ClearAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        var removed = await uow.ExecuteAsync(
            "DELETE FROM file_skip_markers WHERE library_id = @lib AND relative_path = @path",
            ("@lib", libraryId), ("@path", relativePath)).ConfigureAwait(false);
        if (removed > 0)
        {
            Changed(uow);
        }
    }

    /// <summary>Clears one marker by its own row id, for "Process again" on the Kept files list.</summary>
    public async Task ClearByIdAsync(UnitOfWork uow, long id)
    {
        var removed = await uow.ExecuteAsync("DELETE FROM file_skip_markers WHERE id = @id", ("@id", id)).ConfigureAwait(false);
        if (removed > 0)
        {
            Changed(uow);
        }
    }

    private void Changed(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.KeptFiles);
}
