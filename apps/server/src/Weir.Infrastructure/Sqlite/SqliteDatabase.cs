using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace Weir.Infrastructure.Sqlite;

/// <summary>
/// Opens connections to Weir's SQLite file with the same PRAGMAs on every connection: WAL journal,
/// foreign keys on, a 30 second busy timeout and <c>synchronous=NORMAL</c>, plus the cache and journal
/// settings in <see cref="TuningPragmas"/>.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The write gate's SemaphoreSlim never creates a wait handle, so it owns nothing that needs disposing.")]
public sealed class SqliteDatabase
{
    /// <summary>
    /// Processing writes progress in short transactions; a transient writer collision should wait for
    /// that transaction rather than fail a completed media mutation.
    /// </summary>
    /// <remarks>
    /// The contract suite's HTTP client timeout (<c>REQUEST_TIMEOUT_S</c> in its support client module) is
    /// held above this value, 45s against 30s. If the two were equal, a request blocked on the write lock
    /// would have the client give up at the same instant this ceiling expires, and the failure would show up
    /// as a bare <c>httpx.ReadTimeout</c> with no server-side error to go with it (#586). If this value
    /// changes, move that one too, and keep it the larger of the pair.
    /// </remarks>
    public const int BusyTimeoutMilliseconds = 30_000;

    /// <summary>
    /// One gate per connection pool (Microsoft.Data.Sqlite keys its pools by the exact connection string), held
    /// while a pooled handle is handed out and while the pool is cleared (#640).
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite takes a handle from its pool under the pool's lock but activates it outside that lock,
    /// and activation marks the handle in use (<c>_active = true</c>) a moment before it records who holds it. An
    /// open on another thread that finds the pool empty with an even count first reclaims "leaked" connections —
    /// ones in use that nobody holds — and a handle in that moment is one. It goes back on the idle stack and out
    /// again to the second opener, so two connections share one native handle: one's <c>BEGIN IMMEDIATE</c> is
    /// the other's open transaction, and each later returns the handle to the pool while the other may still be
    /// using it. The reclaim only runs inside another open or inside a pool clear, so no open or clear of the same
    /// pool may run while a handle is being activated.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, Lock> PoolGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Settings that stay with a native connection, so they are applied once per handle rather than on every
    /// pooled open (#711): an 8 MB page cache instead of the 2 MB default, temporary tables and sorts in memory,
    /// reads through a 256 MB memory map, and a WAL cut back to 64 MB after each checkpoint instead of keeping
    /// its largest size for ever.
    /// </summary>
    private const string TuningPragmas =
        "PRAGMA cache_size=-8000; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=268435456; PRAGMA journal_size_limit=67108864;";

    /// <summary>The native handles <see cref="TuningPragmas"/> has run on; an entry goes when its handle is closed and collected.</summary>
    private static readonly ConditionalWeakTable<sqlite3, object> TunedHandles = [];

    private static readonly object TunedMarker = new();

    /// <summary>SQLite's result code for a database file that cannot be opened (<c>SQLITE_CANTOPEN</c>).</summary>
    private const int SqliteCantOpen = 14;

    /// <summary>
    /// Weir's own writers queue here, one at a time, before they ask SQLite for its write lock. SQLite makes a writer that
    /// finds the lock taken sleep and retry inside <c>BEGIN IMMEDIATE</c> for up to the busy timeout, and
    /// Microsoft.Data.Sqlite runs even its async calls synchronously, so every waiting writer held a thread-pool thread
    /// for as long as it waited. Waiting here costs no thread, and writers are served in roughly the order they asked
    /// (SemaphoreSlim does not promise it). The busy timeout still covers a writer outside this process, such as a backup
    /// tool. Readers never come here (WAL).
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Connection strings whose pool has been filled with at least one connection, keyed the same way as
    /// <see cref="PoolGates"/>. <c>Mode=ReadWriteCreate</c> may only conjure the database file into existence on
    /// a pool's very first open; once a pool holds a warm connection, every further open must find a real file
    /// still at <see cref="DatabasePath"/> — a pooled native handle otherwise keeps working on one that has been
    /// deleted or replaced by a directory underneath it (Linux lets an open file be unlinked and still read or
    /// written through a descriptor opened before the unlink).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> PoolsWithAFileOnDisk = new(StringComparer.Ordinal);

    private readonly Lock? _poolGate;
    private readonly ILogger? _logger;
    private readonly int _busyTimeoutMilliseconds;
    private readonly TimeProvider _time;

    /// <param name="databasePath">The SQLite file.</param>
    /// <param name="pooling">
    /// Off only for a caller deliberately simulating several independent processes sharing one file in a
    /// single test process (e.g. a claim-concurrency stress test): every <see cref="SqliteDatabase"/>
    /// instance made from the same path otherwise shares one process-wide pool keyed by connection
    /// string, which is exactly right for production (repeated opens reuse a warm native handle) but
    /// means "separate workers" in such a test are not actually isolated the way separate real processes
    /// would be — each gets a genuinely fresh, unshared connection instead.
    /// </param>
    /// <param name="logger">Where a pooled connection found still inside a transaction is reported.</param>
    /// <param name="busyTimeoutMilliseconds">
    /// Overrides <see cref="BusyTimeoutMilliseconds"/>. Production never passes this; a test that needs a
    /// write-lock wait to fail fast, instead of tying up thirty real seconds, can shrink it here.
    /// </param>
    /// <param name="timeProvider">
    /// The clock that times the write gate's wait and its give-up, so a test can pass the busy timeout with no real waiting.
    /// SQLite's own busy handler, for a writer outside this process, always runs on the real clock.
    /// </param>
    public SqliteDatabase(string databasePath, bool pooling = true, ILogger<SqliteDatabase>? logger = null, int? busyTimeoutMilliseconds = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _logger = logger;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds ?? BusyTimeoutMilliseconds;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = pooling,
            // Seconds a command waits on a locked database (complements PRAGMA busy_timeout). It should
            // only ever time a genuine wait for another writer's lock. Microsoft.Data.Sqlite re-runs a
            // statement that answers SQLITE_BUSY until this expires, and SQLITE_BUSY_SNAPSHOT (a
            // read-then-write upgrade on a snapshot another connection has already overtaken) answers
            // exactly that while never being able to succeed, wasting the whole thirty seconds. Units of
            // work take the write lock at BEGIN (UnitOfWork.EnsureTransactionAsync), so no transaction here can
            // hold a stale snapshot (#586).
            DefaultTimeout = _busyTimeoutMilliseconds / 1000,
        }.ToString();
        _poolGate = pooling ? PoolGates.GetOrAdd(ConnectionString, static _ => new Lock()) : null;
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// Releases this database's own pooled connections, e.g. so its file can be deleted once every
    /// caller is done with it. Scoped to <see cref="ConnectionString"/> alone: unlike
    /// <see cref="SqliteConnection.ClearAllPools"/>, it never touches another database's pool, so it is
    /// safe to call while other connections (elsewhere in the process, such as a concurrently running
    /// test) are still open.
    /// </summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(ConnectionString);
        if (_poolGate is null)
        {
            SqliteConnection.ClearPool(connection);
            return;
        }

        lock (_poolGate)
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        // SqliteConnection.OpenAsync is Open() behind a completed task, so nothing is lost by opening synchronously.
        cancellationToken.ThrowIfCancellationRequested();
        var connection = OpenOutsideAnyTransaction();
        try
        {
            await ApplyPragmasAsync(connection, _busyTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Takes a turn at the write gate, waiting for it without holding a thread, and says how long that took. Gives up as
    /// SQLite would, once the busy timeout has passed, with a <see cref="DatabaseBusyException"/> (<c>SQLITE_BUSY</c>) that says how long
    /// the wait was. Whoever takes a turn gives it back with <see cref="ReleaseWriteGate"/> when the write lock is free again.
    /// </summary>
    internal async Task<TimeSpan> AcquireWriteGateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_writeGate.Wait(0, CancellationToken.None))
        {
            return TimeSpan.Zero;
        }

        var started = _time.GetTimestamp();
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(_busyTimeoutMilliseconds), _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, giveUp.Token);
        try
        {
            await _writeGate.WaitAsync(either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DatabaseBusyException(_time.GetElapsedTime(started));
        }

        return _time.GetElapsedTime(started);
    }

    internal void ReleaseWriteGate() => _writeGate.Release();

    /// <summary>
    /// Begins a write transaction (<c>BEGIN IMMEDIATE</c>) on a connection whose caller has the write gate, waiting for a
    /// writer outside this process for no longer than what is left of one busy timeout after <paramref name="waitedForGate"/>,
    /// so a refusal never takes twice the timeout. Microsoft.Data.Sqlite retries a busy statement until the command timeout,
    /// in whole seconds and at least one, so SQLite's own busy wait is switched off meanwhile and the command timeout is
    /// the time left, rounded up to the second. Both are set back afterwards. A refusal is a <see cref="DatabaseBusyException"/>
    /// that says how long the writer waited in all, at the gate and here.
    /// </summary>
    internal async Task<SqliteTransaction> BeginWriteTransactionAsync(SqliteConnection connection, TimeSpan waitedForGate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var started = _time.GetTimestamp();
        try
        {
            return await BeginImmediateAsync(connection, waitedForGate, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception is not DatabaseBusyException && DatabaseBusyException.IsBusy(exception))
        {
            throw new DatabaseBusyException(waitedForGate + _time.GetElapsedTime(started), exception.SqliteErrorCode, exception.SqliteExtendedErrorCode);
        }
    }

    private async Task<SqliteTransaction> BeginImmediateAsync(SqliteConnection connection, TimeSpan waitedForGate, CancellationToken cancellationToken)
    {
        if (waitedForGate < TimeSpan.FromMilliseconds(10))
        {
            return connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        }

        var commandTimeout = connection.DefaultTimeout;
        await ExecuteAsync(connection, "PRAGMA busy_timeout=0", cancellationToken).ConfigureAwait(false);
        connection.DefaultTimeout = CommandTimeoutSeconds(_busyTimeoutMilliseconds, waitedForGate);
        try
        {
            return connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        }
        finally
        {
            connection.DefaultTimeout = commandTimeout;
            await ExecuteAsync(connection, $"PRAGMA busy_timeout={_busyTimeoutMilliseconds}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The command timeout that leaves a writer what is left of one busy timeout after it waited <paramref name="waitedForGate"/>,
    /// in whole seconds and never less than one, as Microsoft.Data.Sqlite counts it.
    /// </summary>
    internal static int CommandTimeoutSeconds(int busyTimeoutMilliseconds, TimeSpan waitedForGate)
    {
        var remaining = (int)Math.Max(1, busyTimeoutMilliseconds - waitedForGate.TotalMilliseconds);
        return Math.Max(1, (remaining + 999) / 1000);
    }

    public SqliteConnection Open()
    {
        var connection = OpenOutsideAnyTransaction();
        try
        {
            ApplyPragmasAsync(connection, _busyTimeoutMilliseconds, CancellationToken.None).GetAwaiter().GetResult();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Dispose <paramref name="connection"/> so that its native handle is closed instead of going back to the pool,
    /// which ends any transaction still open on it: SQLite rolls back what a closed connection left open.
    /// </summary>
    internal void CloseHandle(SqliteConnection connection)
    {
        // That rollback calls any rollback hook still registered on the handle, from inside sqlite3_close_v2. The
        // one Microsoft.Data.Sqlite registers for a live SqliteTransaction touches the handle being closed and
        // throws out through the native frame, which on Linux takes the process down.
        raw.sqlite3_rollback_hook(connection.Handle, null, null);

        // Cleared first, the pool disposes the handle when it comes back rather than keeping it.
        ClearPool();
        connection.Dispose();
    }

    /// <summary>
    /// A pooled handle that is not inside a transaction. Microsoft.Data.Sqlite does nothing to a handle that comes
    /// back to its pool, so one whose last user left a transaction open — a ROLLBACK that failed, a connection
    /// shared by two users (<see cref="PoolGates"/>) — still has it, and after a <c>BEGIN IMMEDIATE</c> still holds
    /// the write lock that every other writer is queueing for (#640).
    /// </summary>
    private SqliteConnection OpenOutsideAnyTransaction()
    {
        var connection = OpenPooledHandle();
        if (raw.sqlite3_get_autocommit(connection.Handle) != 0)
        {
            return connection;
        }

        _logger?.LogWarning(
            "A pooled connection to {DatabasePath} was handed out still inside a transaction; Weir rolled it back " +
            "before using the connection.",
            DatabasePath);
        try
        {
            // If that transaction still has an owner, the rollback hook Microsoft.Data.Sqlite registered for it marks
            // it rolled back, and the owner's next command fails instead of running outside any transaction.
            using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Checked below.
        }

        if (raw.sqlite3_get_autocommit(connection.Handle) != 0)
        {
            return connection;
        }

        CloseHandle(connection);
        return OpenPooledHandle();
    }

    private SqliteConnection OpenPooledHandle()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            if (_poolGate is null)
            {
                connection.Open();
            }
            else
            {
                lock (_poolGate)
                {
                    RefuseIfTheFileIsGone();
                    connection.Open();
                    PoolsWithAFileOnDisk.TryAdd(ConnectionString, true);
                }
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuse to hand out a connection — pooled or fresh — once this pool has been filled and the file it was
    /// filled against is no longer there or no longer a regular file. Existence as a regular file is all this
    /// checks: Weir never swaps its database file under a running server (restoring a backup replaces the
    /// configuration bundle, not this file), so telling one file apart from a different one placed at the same
    /// path is not a case that needs handling. Must run under <see cref="_poolGate"/> so it can never race a
    /// concurrent open filling the pool or <see cref="ClearPool"/> emptying it.
    /// </summary>
    private void RefuseIfTheFileIsGone()
    {
        if (!PoolsWithAFileOnDisk.ContainsKey(ConnectionString) || File.Exists(DatabasePath))
        {
            return;
        }

        PoolsWithAFileOnDisk.TryRemove(ConnectionString, out _);
        ClearPool();
        throw new SqliteException("unable to open database file", SqliteCantOpen);
    }

    /// <summary>
    /// The health probe: the database can still be opened, and <c>SELECT 1</c> answers with a one second busy
    /// timeout, so a long writer makes health slow for at most a second instead of thirty. Never throws.
    /// </summary>
    /// <remarks>
    /// <see cref="OpenAsync"/> itself refuses a connection once the file has been deleted or replaced by a
    /// directory, so this needs no file check of its own.
    /// </remarks>
    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await ExecuteAsync(connection, "PRAGMA busy_timeout=1000", cancellationToken).ConfigureAwait(false);
                try
                {
                    await ExecuteAsync(connection, "SELECT 1", cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await ExecuteAsync(connection, $"PRAGMA busy_timeout={_busyTimeoutMilliseconds}", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, int busyTimeoutMilliseconds, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA busy_timeout={busyTimeoutMilliseconds}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL", cancellationToken).ConfigureAwait(false);
        var handle = connection.Handle!;
        if (!TunedHandles.TryGetValue(handle, out _))
        {
            await ExecuteAsync(connection, TuningPragmas, cancellationToken).ConfigureAwait(false);
            TunedHandles.AddOrUpdate(handle, TunedMarker);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
