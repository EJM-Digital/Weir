using Microsoft.Data.Sqlite;

namespace Weir.Infrastructure.Sqlite;

/// <summary>
/// A write that gave up because the database stayed locked for the whole busy timeout: Weir's own writers queued at the write gate
/// past it, or a writer outside Weir held SQLite's write lock past it. It is a <see cref="SqliteException"/> with SQLite's
/// <c>SQLITE_BUSY</c> code, so everything that already handles a locked database still does, and it says how long the writer waited,
/// which is what the answer to a media manager bases its <c>Retry-After</c> on.
/// </summary>
public sealed class DatabaseBusyException : SqliteException
{
    /// <summary>SQLite's result code for a database that stays locked past the busy timeout (<c>SQLITE_BUSY</c>).</summary>
    public const int BusyCode = 5;

    /// <summary>SQLite's result code for a table locked by another connection (<c>SQLITE_LOCKED</c>).</summary>
    public const int LockedCode = 6;

    public DatabaseBusyException(TimeSpan waited)
        : base("database is locked", BusyCode)
    {
        Waited = waited;
    }

    public DatabaseBusyException(TimeSpan waited, int errorCode, int extendedErrorCode)
        : base("database is locked", errorCode, extendedErrorCode)
    {
        Waited = waited;
    }

    /// <summary>How long the writer waited before it gave up.</summary>
    public TimeSpan Waited { get; }

    /// <summary><c>database is locked</c> (<c>SQLITE_BUSY</c>) or <c>database table is locked</c> (<c>SQLITE_LOCKED</c>), at any extended code.</summary>
    public static bool IsBusy(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is BusyCode or LockedCode;
    }
}
