using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// Weir's own writers queue for the write lock without holding a thread, one at a time, and a unit of work gives its turn back
/// however it ends. Every wait has a limit, so a broken gate fails a test instead of hanging it. Nothing here measures how fast
/// the process's shared thread pool answers: that depends on every other test running at the same time, not on the gate.
/// </summary>
public sealed class UnitOfWorkWriteGateTests
{
    private const string InsertJob =
        "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts) VALUES (@dedupe, 'processing.remux_pass', 'pending', 3)";

    private const int BusyTimeoutMilliseconds = 2000;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>For a test that expects a writer to be refused when the busy timeout passes.</summary>
    private static SqliteDatabase ShortTimeout(JobsTestDatabase db) => new(db.DbPath, busyTimeoutMilliseconds: BusyTimeoutMilliseconds);

    /// <summary>
    /// For a test in which every writer is meant to get its turn: the production busy timeout, so that no writer is refused
    /// because a busy machine was slow to hand the lock on. A broken gate is caught by what the test asserts, not by a timeout.
    /// </summary>
    private static SqliteDatabase Patient(JobsTestDatabase db) => new(db.DbPath);

    private static async Task WriteAsync(SqliteDatabase database, string dedupeKey, CancellationToken cancellationToken = default)
    {
        var uow = await UnitOfWork.OpenAsync(database, cancellationToken);
        await using (uow.ConfigureAwait(false))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", dedupeKey));
            await uow.CommitAsync();
        }
    }

    /// <summary>
    /// A unit that has written and keeps its turn until <paramref name="release"/> completes. It takes its turn on the calling thread,
    /// which is free to take it, so getting there needs nothing from the thread pool.
    /// </summary>
    private static async Task<Task> HoldTheLockAsync(SqliteDatabase database, string dedupeKey, TaskCompletionSource release)
    {
        var holding = new TaskCompletionSource();
        var holder = HoldAsync();
        await (await Task.WhenAny(holding.Task, holder).WaitAsync(Limit));
        return holder;

        async Task HoldAsync()
        {
            var uow = await UnitOfWork.OpenAsync(database);
            await using (uow.ConfigureAwait(false))
            {
                await uow.ExecuteAsync(InsertJob, ("@dedupe", dedupeKey));
                holding.SetResult();
                await release.Task;
                await uow.CommitAsync();
            }
        }
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
    /// A writer that waited for the lock in SQLite's busy handler, or on the gate with a blocking wait, would keep its thread for as
    /// long as it waited. Every writer here starts on a scheduler with one thread of its own while another unit holds the lock, so
    /// a writer that held its thread would keep the writers behind it from ever starting, and one that gave it up lets every writer
    /// get as far as asking for its turn. The thread pool is not involved, so nothing running beside this test can disturb the answer.
    /// </summary>
    [Fact]
    public async Task Writers_waiting_for_the_lock_hold_no_threads_and_all_get_their_turn()
    {
        using var db = new JobsTestDatabase();
        var database = Patient(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await HoldTheLockAsync(database, "holder", release);

        // Eight writers for the one thread there is.
        using var oneThread = new OneThreadScheduler();
        var waiting = Enumerable.Range(0, 8).ToArray();
        var asking = waiting
            .Select(index => Task.Factory.StartNew(() => WriteAsync(database, $"waiting-{index}"), CancellationToken.None, TaskCreationOptions.DenyChildAttach, oneThread))
            .ToArray();
        var writers = asking.Select(asked => asked.Unwrap()).ToArray();
        try
        {
            // Each of these is done when its writer first gave the thread up: that is where it waits for the gate, because the unit
            // holding the lock has not let go of it. A writer that blocked instead would keep the thread from the writers behind it, and
            // this wait would not finish.
            await Task.WhenAll(asking).WaitAsync(Limit);
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
        var database = Patient(db);
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
            var refused = await Assert.ThrowsAsync<SqliteException>(() => WriteAsync(database, "too-late")).WaitAsync(Limit);
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

            var refused = await Assert.ThrowsAsync<SqliteException>(() => WriteAsync(database, "inner")).WaitAsync(Limit);
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

            var refused = await Assert.ThrowsAsync<SqliteException>(
                () => database.BeginWriteTransactionAsync(connection, TimeSpan.FromMilliseconds(2500), CancellationToken.None)).WaitAsync(Limit);

            Assert.Equal(5, refused.SqliteErrorCode);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(3500));
        }
        finally
        {
            await using var rollback = outsider.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
    }
}
