using Microsoft.Data.Sqlite;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// SQLite's write lock on a running server's database, held by this test as a backup tool or any other process outside
/// Weir would hold it. The server stays up and keeps answering; every write it is asked for waits for the lock and, once
/// its busy timeout has passed, gives up. Disposing lets go of the lock.
/// </summary>
public sealed class HeldWriteLock : IDisposable
{
    private readonly SqliteConnection _connection;

    private HeldWriteLock(string databasePath)
    {
        // No pooling: a pooled connection would keep the transaction, and the lock, after Dispose.
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        _connection.Open();
        using var begin = _connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        begin.ExecuteNonQuery();
    }

    public static HeldWriteLock Take(WeirServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new HeldWriteLock(server.DatabasePath);
    }

    public void Dispose()
    {
        using (var rollback = _connection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        _connection.Dispose();
    }
}
