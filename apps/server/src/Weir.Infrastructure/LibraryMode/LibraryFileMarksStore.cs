using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>What is true of one library file beyond the scan's own verdict: that Weir cleaned it, and that a person
/// told Weir to leave it alone.</summary>
public sealed record LibraryFileMark(string Path, DateTimeOffset? CleanedAt, bool LeaveAlone);

/// <summary>
/// The <c>library_file_marks</c> table (migration 0012). It exists because a scan rewrites <c>library_files</c> from
/// scratch every time: anything a person decided, or anything Weir did rather than observed, has to be kept outside
/// that picture or it lasts only until the next scan.
/// </summary>
public sealed class LibraryFileMarksStore
{
    /// <summary>Weir finished cleaning this file, now.</summary>
    public Task MarkCleanedAsync(UnitOfWork uow, long libraryId, string path, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "INSERT INTO library_file_marks (library_id, path, cleaned_at, updated_at) VALUES (@library, @path, @when, @when) " +
            "ON CONFLICT (library_id, path) DO UPDATE SET cleaned_at = excluded.cleaned_at, updated_at = excluded.updated_at",
            ("@library", libraryId),
            ("@path", path),
            ("@when", TimestampColumns.Orm(when)));
    }

    /// <summary>A person asked Weir to leave this file alone, or changed their mind.</summary>
    public Task SetLeaveAloneAsync(UnitOfWork uow, long libraryId, string path, bool leaveAlone, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "INSERT INTO library_file_marks (library_id, path, leave_alone, leave_alone_at, updated_at) " +
            "VALUES (@library, @path, @leave, @at, @when) " +
            "ON CONFLICT (library_id, path) DO UPDATE SET leave_alone = excluded.leave_alone, " +
            "leave_alone_at = excluded.leave_alone_at, updated_at = excluded.updated_at",
            ("@library", libraryId),
            ("@path", path),
            ("@leave", leaveAlone),
            ("@at", leaveAlone ? TimestampColumns.Orm(when) : null),
            ("@when", TimestampColumns.Orm(when)));
    }

    /// <summary>Whether this file is one Weir has been told to leave alone.</summary>
    public async Task<bool> IsLeftAloneAsync(UnitOfWork uow, long libraryId, string path)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var mark = await FindAsync(uow, libraryId, path).ConfigureAwait(false);
        return mark?.LeaveAlone ?? false;
    }

    /// <summary>One file's marks, or null when Weir has nothing to say about it yet.</summary>
    public Task<LibraryFileMark?> FindAsync(UnitOfWork uow, long libraryId, string path)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync(
            "SELECT path, cleaned_at, leave_alone FROM library_file_marks WHERE library_id = @library AND path = @path LIMIT 1",
            Read,
            ("@library", libraryId),
            ("@path", path));
    }

    /// <summary>Every marked file in one library, for a caller that is about to look at many of them.</summary>
    public async Task<IReadOnlyDictionary<string, LibraryFileMark>> ForLibraryAsync(UnitOfWork uow, long libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync(
            "SELECT path, cleaned_at, leave_alone FROM library_file_marks WHERE library_id = @library",
            Read,
            ("@library", libraryId)).ConfigureAwait(false);
        var marks = new Dictionary<string, LibraryFileMark>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            marks[row.Path] = row;
        }

        return marks;
    }

    private static LibraryFileMark Read(SqliteDataReader reader) => new(
        SqliteValues.GetString(reader, 0),
        TimestampColumns.Parse(reader.GetValue(1)),
        SqliteValues.GetBool(reader, 2));
}
