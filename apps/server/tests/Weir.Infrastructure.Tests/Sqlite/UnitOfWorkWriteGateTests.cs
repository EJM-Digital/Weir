using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// Weir's own writers queue for the write lock without holding a thread, one at a time, and a unit of work gives its turn back
/// however it ends. Every database here has a short busy timeout and every wait a limit, so a broken gate fails a test
/// instead of hanging it.
/// </summary>
public sealed class UnitOfWorkWriteGateTests
{
    private const string InsertJob =
        "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts) VALUES (@dedupe, 'processing.remux_pass', 'pending', 3)";

    private const int BusyTimeoutMilliseconds = 2000;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static SqliteDatabase ShortTimeout(JobsTestDatabase db) => new(db.DbPath, busyTimeoutMilliseconds: BusyTimeoutMilliseconds);

    private static async Task WriteAsync(SqliteDatabase database, string dedupeKey, CancellationToken cancellationToken = default)
    {
        var uow = await UnitOfWork.OpenAsync(database, cancellationToken);
        await using (uow.ConfigureAwait(false))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", dedupeKey));
            await uow.CommitAsync();
        }
    }

    /// <summary>A unit that has written and keeps its turn until <paramref name="release"/> completes.</summary>
    private static async Task<Task> HoldTheLockAsync(SqliteDatabase database, string dedupeKey, TaskCompletionSource release)
    {
        var holding = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            var uow = await UnitOfWork.OpenAsync(database);
            await using (uow.ConfigureAwait(false))
            {
                await uow.ExecuteAsync(InsertJob, ("@dedupe", dedupeKey));
                holding.SetResult();
                await release.Task;
                await uow.CommitAsync();
            }
        });
        await holding.Task.WaitAsync(Limit);
        return holder;
    }

    /// <summary>Waits for tasks to end, however they end, so nothing is still using a database when a test lets go of it.</summary>
    private static async Task SettleAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(Limit);
        }
#pragma warning disable CA1031 // Only waiting for the tasks to end; the test asserts what they ended with.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Whether the thread pool still runs work promptly, asked from a thread of its own so that a pool with no free thread cannot
    /// stop the question being put.
    /// </summary>
    private static Task<bool> PoolRunsWorkPromptlyAsync() =>
        Task.Factory.StartNew(
            () => Task.Run(() => 42).Wait(TimeSpan.FromSeconds(1)),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    [Fact]
    public async Task Writers_waiting_for_the_lock_hold_no_threads_and_all_get_their_turn()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);

        // More than the pool's minimum: threads blocked in SQLite's busy handler would stall everything else.
        var waiting = Enumerable.Range(0, Environment.ProcessorCount * 4 + 8).ToArray();
        var writers = waiting.Select(index => Task.Run(() => WriteAsync(database, $"waiting-{index}"))).ToArray();
        try
        {
            // Long enough for every writer to have asked for its turn.
            await Task.Delay(500);
            Assert.True(await PoolRunsWorkPromptlyAsync().WaitAsync(Limit), "the thread pool had no thread to spare");
            Assert.All(writers, writer => Assert.False(writer.IsCompleted));

            release.SetResult();
            await Task.WhenAll(writers.Append(holder)).WaitAsync(Limit);

            Assert.Equal(waiting.Length + 1L, db.Count("SELECT count(DISTINCT dedupe_key) FROM jobs"));
        }
        finally
        {
            release.TrySetResult();
            await SettleAsync(writers.Append(holder));
        }
    }

    [Fact]
    public async Task A_unit_that_fails_gives_its_turn_to_the_next_writer()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var uow = await UnitOfWork.OpenAsync(database);
            await uow.ExecuteAsync("INSERT INTO no_such_table VALUES (1)");
        }).WaitAsync(Limit);

        await WriteAsync(database, "next").WaitAsync(Limit);
        Assert.Equal(1L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_unit_disposed_without_committing_gives_its_turn_to_the_next_writer()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        await using (var uow = await UnitOfWork.OpenAsync(database))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "abandoned"));
        }

        await WriteAsync(database, "next").WaitAsync(Limit);
        Assert.Equal(1L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_unit_that_commits_gives_its_turn_up_before_its_after_commit_callbacks_run()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        bool? writtenFromCallback = null;
        await using (var uow = await UnitOfWork.OpenAsync(database))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "first"));

            // The callback waits for another writer, which can only get in if the commit has already let go of the turn.
            uow.OnCommitted(() =>
            {
                try
                {
                    writtenFromCallback = WriteAsync(database, "from-the-callback").Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    writtenFromCallback = false;
                }
            });
            await uow.CommitAsync();
        }

        Assert.True(writtenFromCallback, "the writer in the callback was still waiting for the committed unit's turn");
        Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_unit_cancelled_after_it_wrote_gives_its_turn_to_the_next_writer()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        using var cancel = new CancellationTokenSource();
        await using (var uow = await UnitOfWork.OpenAsync(database, cancel.Token))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "cancelled"));
            await cancel.CancelAsync();
        }

        await WriteAsync(database, "next").WaitAsync(Limit);
        Assert.Equal(1L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_writer_cancelled_while_it_waits_leaves_the_queue_without_taking_a_turn()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);
        using var cancel = new CancellationTokenSource();
        var cancelled = WriteAsync(database, "cancelled", cancel.Token);
        var next = WriteAsync(database, "next");
        try
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled).WaitAsync(Limit);

            release.SetResult();
            await Task.WhenAll(holder, next).WaitAsync(Limit);
            Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
        }
        finally
        {
            release.TrySetResult();
            await SettleAsync([holder, cancelled, next]);
        }
    }

    [Fact]
    public async Task A_writer_still_waiting_when_the_busy_timeout_passes_fails_as_SQLite_would()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);
        try
        {
            var refused = await Assert.ThrowsAsync<DatabaseBusyException>(() => WriteAsync(database, "too-late")).WaitAsync(Limit);
            Assert.Equal(5, refused.SqliteErrorCode);

            release.SetResult();
            await holder.WaitAsync(Limit);
            await WriteAsync(database, "after").WaitAsync(Limit);
            Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
        }
        finally
        {
            release.TrySetResult();
            await SettleAsync([holder]);
        }
    }

    [Fact]
    public async Task A_second_unit_opened_after_the_first_commits_is_not_held_up()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        var outer = await UnitOfWork.OpenAsync(database);
        await using (outer.ConfigureAwait(false))
        {
            await outer.CountAsync("SELECT count(*) FROM jobs");
            await outer.ExecuteAsync(InsertJob, ("@dedupe", "outer"));
            await outer.CommitAsync();

            await WriteAsync(database, "inner").WaitAsync(Limit);
        }

        Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
    }

    /// <summary>
    /// A flow that opens a second writing unit while its first still holds the lock cannot succeed, with or without the gate: it
    /// is refused when the busy timeout passes, never left waiting for ever.
    /// </summary>
    [Fact]
    public async Task A_second_unit_that_writes_while_the_first_holds_the_lock_is_refused_not_hung()
    {
        using var db = new JobsTestDatabase();
        var database = ShortTimeout(db);
        var outer = await UnitOfWork.OpenAsync(database);
        await using (outer.ConfigureAwait(false))
        {
            await outer.ExecuteAsync(InsertJob, ("@dedupe", "outer"));

            var refused = await Assert.ThrowsAsync<DatabaseBusyException>(() => WriteAsync(database, "inner")).WaitAsync(Limit);
            Assert.Equal(5, refused.SqliteErrorCode);

            await outer.CommitAsync();
        }

        Assert.Equal(1L, db.Count("SELECT count(*) FROM jobs"));
    }

    /// <summary>
    /// After waiting for the gate, a writer waits for a writer outside Weir only for what is left of one busy timeout, so a refusal
    /// never takes the timeout twice.
    /// </summary>
    [Fact]
    public async Task The_wait_for_an_outside_writer_is_what_is_left_of_one_busy_timeout()
    {
        using var db = new JobsTestDatabase();
        var database = new SqliteDatabase(db.DbPath, busyTimeoutMilliseconds: 4000);
        await using var outsider = new SqliteConnection(database.ConnectionString);
        await outsider.OpenAsync();
        await using (var begin = outsider.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        try
        {
            await using var connection = await database.OpenAsync();
            var started = Stopwatch.StartNew();

            var refused = await Assert.ThrowsAsync<DatabaseBusyException>(
                () => database.BeginWriteTransactionAsync(connection, TimeSpan.FromMilliseconds(2500), CancellationToken.None)).WaitAsync(Limit);

            Assert.Equal(5, refused.SqliteErrorCode);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(3500));

            // What it says it waited is the time at the gate it was told of plus the time it really spent waiting for the outside writer.
            Assert.InRange(refused.Waited, TimeSpan.FromMilliseconds(2500) + TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2500) + TimeSpan.FromMilliseconds(3500));
        }
        finally
        {
            await using var rollback = outsider.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The gate's give-up is timed by the database's <see cref="TimeProvider"/>, so this proves what a refused writer reports with no real
    /// waiting: the writer is still a <see cref="SqliteException"/> with SQLite's busy code, for the callers that already handle one, and it
    /// carries how long it waited, for the answer to a media manager (#964).
    /// </summary>
    [Fact]
    public async Task A_writer_refused_at_the_gate_reports_the_time_it_waited_on_the_databases_own_clock()
    {
        using var db = new JobsTestDatabase();
        var time = new FakeTimeProvider();
        var database = new SqliteDatabase(db.DbPath, busyTimeoutMilliseconds: SqliteDatabase.BusyTimeoutMilliseconds, timeProvider: time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);
        try
        {
            var waiting = WriteAsync(database, "too-late");
            time.Advance(TimeSpan.FromMilliseconds(SqliteDatabase.BusyTimeoutMilliseconds - 1));
            Assert.False(waiting.IsCompleted);

            time.Advance(TimeSpan.FromMilliseconds(1));
            var refused = await Assert.ThrowsAsync<DatabaseBusyException>(() => waiting).WaitAsync(Limit);

            Assert.IsAssignableFrom<SqliteException>(refused);
            Assert.Equal(5, refused.SqliteErrorCode);
            Assert.True(LockedWrites.IsLock(refused));
            Assert.Equal(TimeSpan.FromMilliseconds(SqliteDatabase.BusyTimeoutMilliseconds), refused.Waited);
        }
        finally
        {
            release.TrySetResult();
            await SettleAsync([holder]);
        }
    }

    [Fact]
    public async Task A_writer_cancelled_at_the_gate_is_cancelled_not_refused_as_busy()
    {
        using var db = new JobsTestDatabase();
        var time = new FakeTimeProvider();
        var database = new SqliteDatabase(db.DbPath, busyTimeoutMilliseconds: SqliteDatabase.BusyTimeoutMilliseconds, timeProvider: time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);
        using var cancel = new CancellationTokenSource();
        try
        {
            var waiting = WriteAsync(database, "cancelled", cancel.Token);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WaitAsync(Limit);
        }
        finally
        {
            release.TrySetResult();
            await SettleAsync([holder]);
        }
    }
}
