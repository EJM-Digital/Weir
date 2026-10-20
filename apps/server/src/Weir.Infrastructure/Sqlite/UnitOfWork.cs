using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Weir.Core.Json;
using Weir.Core.Time;

namespace Weir.Infrastructure.Sqlite;

/// <summary>
/// One request's database work: reads run outside a transaction until the first write, which begins one;
/// the owner commits on success, and anything uncommitted is rolled back on dispose. That transaction is always <c>BEGIN IMMEDIATE</c> — see <see cref="EnsureTransactionAsync"/> for why a
/// deferred one could not survive another writer committing mid-unit (#586).
/// </summary>
/// <remarks>
/// The only transaction mechanism server-wide: every query, command and commit that runs
/// inside a unit of work uses the <see cref="CancellationToken"/> it was opened with — there is one token per
/// unit, not one per call, since every operation in a unit belongs to the same request or background pass and
/// a caller that wants different cancellation semantics for part of its work opens a second unit of work with
/// a different token (as the library clean handler's own-transaction history write already does). See
/// <see cref="RollbackAsync"/> for why a rollback does not use it.
/// </remarks>
public sealed class UnitOfWork : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<SqliteTransaction, UnitOfWork> Owners = [];

    private readonly CancellationToken _cancellationToken;
    private SqliteTransaction? _transaction;
    private List<Action>? _afterCommit;
    private Dictionary<string, object>? _items;
    private bool _holdsWriteGate;

    private UnitOfWork(SqliteDatabase database, SqliteConnection connection, CancellationToken cancellationToken)
    {
        Database = database;
        Connection = connection;
        _cancellationToken = cancellationToken;
    }

    public SqliteDatabase Database { get; }

    public SqliteConnection Connection { get; }

    public bool InTransaction => _transaction is not null;

    /// <summary>Per-unit state for writers; cleared when the unit commits or rolls back.</summary>
    public IDictionary<string, object> Items => _items ??= new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// The unit of work that began <paramref name="transaction"/>, for code handed only a connection and a transaction (the
    /// job queue's enqueue) that must act once the unit commits. Null for a transaction no unit of work began.
    /// </summary>
    public static UnitOfWork? OwnerOf(SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return Owners.TryGetValue(transaction, out var owner) ? owner : null;
    }

    public static async Task<UnitOfWork> OpenAsync(SqliteDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new UnitOfWork(database, connection, cancellationToken);
    }

    /// <summary>
    /// Run <paramref name="callback"/> once, after the next successful commit; a rollback discards it.
    /// </summary>
    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        (_afterCommit ??= []).Add(callback);
    }

    /// <summary>
    /// Begin the transaction now, before this unit has read or written anything (the bootstrap lock).
    /// Every transaction here is <c>BEGIN IMMEDIATE</c> (see <see cref="EnsureTransactionAsync"/>); this is
    /// for a caller that wants the write lock held across its reads as well, and it must therefore come
    /// before them.
    /// </summary>
    public async Task BeginImmediateAsync()
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        await EnsureTransactionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Begin this unit's transaction now, as a deferred <c>BEGIN</c>: no write lock, and every read that
    /// follows shares one consistent snapshot (#636) instead of each running as its own short autocommit read.
    /// For a caller that issues several reads on <see cref="Connection"/> directly and needs them to agree with
    /// each other without contending with writers. Never write inside a unit begun this way: <see cref="EnsureTransactionAsync"/>
    /// would keep the deferred transaction rather than upgrading it, reintroducing the <c>SQLITE_BUSY_SNAPSHOT</c>
    /// failure that <see cref="EnsureTransactionAsync"/>'s own remarks describe (#586).
    /// </summary>
    public void BeginRead()
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        _transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        Owners.Add(_transaction, this);
    }

    /// <summary>
    /// The write transaction, begun now if this unit of work has not written yet: for code that issues its own
    /// commands on <see cref="Connection"/> as part of this unit of work (the job queue's enqueue). Such code
    /// may read before it writes, so this is the call that most depends on the transaction being immediate
    /// (<see cref="EnsureTransactionAsync"/>, #586); ask for it at the point of the write, not before.
    /// </summary>
    public async Task<SqliteTransaction> WriteTransactionAsync()
    {
        await EnsureTransactionAsync().ConfigureAwait(false);
        return _transaction!;
    }

    /// <summary>The transaction begun by <see cref="BeginRead"/>, for a caller that issues its own commands on <see cref="Connection"/>.</summary>
    public SqliteTransaction ReadTransaction() =>
        _transaction ?? throw new InvalidOperationException("Call BeginRead() first.");

    public async Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await EnsureTransactionAsync().ConfigureAwait(false);
        var command = Create(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(_cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A write that returns a value (<c>INSERT … RETURNING</c> or <c>last_insert_rowid()</c>).</summary>
    public async Task<object?> ExecuteScalarWriteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await EnsureTransactionAsync().ConfigureAwait(false);
        var command = Create(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(_cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Create(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(_cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<long> CountAsync(string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(sql, parameters).ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture);

    public async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        var command = Create(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(_cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<T>();
                while (await reader.ReadAsync(_cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
        }
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
        where T : class
    {
        var rows = await QueryAsync(sql, map, parameters).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task CommitAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(_cancellationToken).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
            ReleaseWriteGate();
        }

        var callbacks = _afterCommit;
        _afterCommit = null;
        _items = null;
        foreach (var callback in callbacks ?? [])
        {
            callback();
        }
    }

    /// <summary>
    /// Roll back and drop this unit's transaction, if it has one. Runs on dispose whenever the unit was never
    /// committed, so this is what an exception or a cancelled operation actually undoes.
    /// </summary>
    /// <remarks>
    /// A caller whose own operation was cancelled reaches this with a token that is already cancelled, but the
    /// rollback itself must still complete — a ROLLBACK abandoned half-way leaves the transaction, and the
    /// write lock it may hold, open on a connection that is about to go back into the pool (#640). There is
    /// nothing left worth cancelling at this point, so the rollback never takes the unit's token: it runs with
    /// <see cref="CancellationToken.None"/>, whether or not that token is cancelled.
    /// </remarks>
    public async Task RollbackAsync()
    {
        _afterCommit = null;
        _items = null;
        if (_transaction is null)
        {
            return;
        }

        try
        {
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            ReleaseWriteGate();
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RollbackAsync().ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Whether the transaction survived is checked below; the error itself changes nothing here.
        }
        finally
        {
            // A ROLLBACK that failed leaves the transaction open, and closing a pooled connection does not end it:
            // the handle goes back to the pool as it is, BEGIN IMMEDIATE's write lock included (#640).
            try
            {
                if (Connection.State == ConnectionState.Open && raw.sqlite3_get_autocommit(Connection.Handle) == 0)
                {
                    Database.CloseHandle(Connection);
                }
                else
                {
                    await Connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // After the connection, so the next writer finds the write lock free: a rollback that failed was only made
                // good just above.
                ReleaseWriteGate();
            }
        }
    }

    /// <summary>
    /// Begin this unit's transaction, once, as <c>BEGIN IMMEDIATE</c>.
    /// </summary>
    /// <remarks>
    /// A deferred transaction takes the write lock at its first write, so when its first statement is a
    /// read instead — which is what <see cref="WriteTransactionAsync"/> hands out, and what the job queue's
    /// enqueue does with it: look the dedupe key up, then insert — the connection's WAL read snapshot is
    /// pinned at that read. If any other connection commits a real write before the upgrade to a writer,
    /// SQLite refuses it with <c>SQLITE_BUSY_SNAPSHOT</c>. That is not a wait-and-retry condition and
    /// <c>busy_timeout</c> cannot clear it: the snapshot is permanently stale, so waiting can never make it
    /// current. Microsoft.Data.Sqlite nonetheless retries the statement until the command timeout
    /// (<see cref="SqliteDatabase.BusyTimeoutMilliseconds"/>) expires, so a single intervening commit — a
    /// real one; a write that changes no rows does not invalidate a snapshot — would turn a request into
    /// thirty seconds of nothing followed by <c>database is locked</c> (#586).
    ///
    /// Taking the write lock at <c>BEGIN</c> removes the window rather than coping with it: while this unit
    /// holds the lock nobody else can commit, so its snapshot cannot go stale and that error is unreachable.
    /// It costs nothing on the common path, where the first statement of the transaction is the write that
    /// began it and the lock would have been taken on the very next statement anyway; where it does move the
    /// acquisition earlier, it is by the handful of rows a store reads before its first insert. Reads before
    /// any transaction exists are untouched — they run in autocommit and still never take the write lock.
    ///
    /// Two writers both waiting at <c>BEGIN IMMEDIATE</c> cannot deadlock: SQLite has exactly one write lock
    /// per database, it is acquired whole in one step rather than accumulated, and a waiter that does not get
    /// it within <c>busy_timeout</c> fails instead of holding anything. Every transaction in this server is
    /// immediate.
    /// </remarks>
    private async Task EnsureTransactionAsync()
    {
        if (_transaction is not null)
        {
            return;
        }

        // Weir's own writers wait their turn here without holding a thread; BEGIN IMMEDIATE below then only waits, in
        // SQLite's busy handler, for a writer outside this process (see SqliteDatabase.AcquireWriteGateAsync). A unit that
        // holds its turn while another unit of the same flow waits for one of its own would wait out the busy timeout, as
        // two connections always have: such a flow commits the first unit before it opens the second.
        var waited = await Database.AcquireWriteGateAsync(_cancellationToken).ConfigureAwait(false);
        _holdsWriteGate = true;
        try
        {
            _transaction = await Database.BeginWriteTransactionAsync(Connection, waited, _cancellationToken).ConfigureAwait(false);
            Owners.Add(_transaction, this);
        }
        catch
        {
            ReleaseWriteGate();
            throw;
        }
    }

    /// <summary>Gives up this unit's turn at the write lock, if it has one. Safe to call again.</summary>
    private void ReleaseWriteGate()
    {
        if (_holdsWriteGate)
        {
            _holdsWriteGate = false;
            Database.ReleaseWriteGate();
        }
    }

    private SqliteCommand Create(string sql, (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}

/// <summary>Reading and writing column values in the storage formats existing Weir databases hold.</summary>
public static class SqliteValues
{
    /// <summary>SQLite's constraint violation result code (<c>SQLITE_CONSTRAINT</c>).</summary>
    public const int SqliteConstraint = 19;

    public static bool IsIntegrityError(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode == SqliteConstraint;
    }

    public static object ToSqlite(Timestamp value) => value.ToSqlite();

    public static object ToSqlite(Timestamp? value) => value is { } v ? v.ToSqlite() : DBNull.Value;

    public static string? GetStringOrNull(SqliteDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static string GetString(SqliteDataReader reader, int ordinal) => GetStringOrNull(reader, ordinal) ?? string.Empty;

    public static long GetInt64(SqliteDataReader reader, int ordinal) => GetInt64OrNull(reader, ordinal) ?? 0;

    /// <summary>A stored integer, or null for NULL. Other stored types read as they do in <see cref="GetInt64"/>.</summary>
    public static long? GetInt64OrNull(SqliteDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            long l => l,
            double d => (long)d,
            string s when long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>A stored boolean: any non-zero, non-empty value reads as true; NULL reads as false.</summary>
    public static bool GetBool(SqliteDataReader reader, int ordinal) => GetBoolOrNull(reader, ordinal) ?? false;

    public static bool? GetBoolOrNull(SqliteDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : WireConvert.FromDatabase(reader.GetValue(ordinal)).IsTruthy;
    }

    /// <summary>A stored timestamp, parsed from its ISO 8601 text; a value that does not parse throws.</summary>
    public static Timestamp? GetDateTimeOrNull(SqliteDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!;
        return Timestamp.TryFromIsoFormat(text, out var value)
            ? value
            : throw new FormatException($"Invalid isoformat string: {WireStrings.Repr(text)}");
    }

    public static Timestamp GetDateTime(SqliteDataReader reader, int ordinal) =>
        GetDateTimeOrNull(reader, ordinal) ?? throw new FormatException("A required timestamp column is NULL.");
}
