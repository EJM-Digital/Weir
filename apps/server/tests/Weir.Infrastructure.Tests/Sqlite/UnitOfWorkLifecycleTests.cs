using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// #745: <see cref="UnitOfWork"/> is the only transaction mechanism server-wide, so this pins down its
/// commit, rollback, cancellation and reuse behaviour directly rather than through one of its many callers.
/// </summary>
public sealed class UnitOfWorkLifecycleTests
{
    private const string InsertJob =
        "INSERT INTO jobs (dedupe_key, job_kind, status, max_attempts) VALUES (@dedupe, 'processing.remux_pass', 'pending', 3)";

    [Fact]
    public async Task Commit_persists_every_write_made_in_the_unit()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "first"));
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "second"));
            await uow.CommitAsync();
        }

        Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_unit_that_only_read_and_whose_connection_was_closed_under_it_is_disposed_without_complaint()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await uow.Connection.CloseAsync();

        await uow.DisposeAsync();

        await using var next = await UnitOfWork.OpenAsync(db.Database);
        await next.ExecuteAsync(InsertJob, ("@dedupe", "after"));
        await next.CommitAsync();
        Assert.Equal(1L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task An_exception_before_commit_rolls_back_every_write_made_in_the_unit()
    {
        using var db = new JobsTestDatabase();

        var thrown = await Record.ExceptionAsync(async () =>
        {
            await using var uow = await UnitOfWork.OpenAsync(db.Database);
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "orphaned"));
            throw new InvalidOperationException("the caller failed after writing but before committing");
        });

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(0L, db.Count("SELECT count(*) FROM jobs"));
    }

    /// <summary>
    /// A request that is cancelled after it has written must still roll back cleanly: the rollback itself
    /// does not propagate the caller's own cancellation (see <see cref="UnitOfWork.RollbackAsync"/>), so the
    /// write is undone and the write lock is free for the next caller rather than stranded on a pooled handle.
    /// </summary>
    [Fact]
    public async Task A_cancelled_unit_still_rolls_back_and_frees_the_write_lock()
    {
        using var db = new JobsTestDatabase();
        using var cancellation = new CancellationTokenSource();

        var uow = await UnitOfWork.OpenAsync(db.Database, cancellation.Token);
        await uow.ExecuteAsync(InsertJob, ("@dedupe", "cancelled-before-commit"));

        cancellation.Cancel();

        // Disposing without a commit is what a cancelled request does: DisposeAsync rolls back, and that
        // rollback must complete despite the token above already being cancelled.
        var disposeException = await Record.ExceptionAsync(async () => await uow.DisposeAsync());
        Assert.Null(disposeException);

        Assert.Equal(0L, db.Count("SELECT count(*) FROM jobs"));
        Assert.True(ForeignWriteSucceedsWithoutWaiting(db), "a cancelled unit of work must not leave the write lock held");
    }

    /// <summary>
    /// Explicit rollback behaves the same way as a cancelled dispose: it completes and frees the lock even
    /// when the unit's own token is already cancelled.
    /// </summary>
    [Fact]
    public async Task Rollback_after_cancellation_completes_and_frees_the_write_lock()
    {
        using var db = new JobsTestDatabase();
        using var cancellation = new CancellationTokenSource();

        var uow = await UnitOfWork.OpenAsync(db.Database, cancellation.Token);
        await using (uow.ConfigureAwait(false))
        {
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "rolled-back-after-cancel"));
            cancellation.Cancel();

            var rollbackException = await Record.ExceptionAsync(async () => await uow.RollbackAsync());
            Assert.Null(rollbackException);
            Assert.False(uow.InTransaction);
        }

        Assert.Equal(0L, db.Count("SELECT count(*) FROM jobs"));
        Assert.True(ForeignWriteSucceedsWithoutWaiting(db), "a rollback after cancellation must not leave the write lock held");
    }

    /// <summary>Every call within one unit shares its single transaction: asking for it again never begins a second one.</summary>
    [Fact]
    public async Task Asking_for_the_write_transaction_more_than_once_reuses_the_same_transaction()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            var first = await uow.WriteTransactionAsync();
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "one"));
            var second = await uow.WriteTransactionAsync();
            await uow.ExecuteAsync(InsertJob, ("@dedupe", "two"));

            Assert.Same(first, second);
            await uow.CommitAsync();
        }

        Assert.Equal(2L, db.Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Beginning_the_transaction_twice_is_refused()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            await uow.BeginImmediateAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(uow.BeginImmediateAsync);
            Assert.Throws<InvalidOperationException>(uow.BeginRead);
        }
    }

    /// <summary>A unit begun for reading never takes the write lock, whatever else is running against the database.</summary>
    [Fact]
    public async Task A_unit_begun_for_reading_never_takes_the_write_lock()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            uow.BeginRead();
            var transaction = uow.ReadTransaction();
            Assert.NotNull(transaction);
            Assert.True(ForeignWriteSucceedsWithoutWaiting(db), "a read-only unit must not block a writer");
        }
    }

    /// <summary>Code handed only the read transaction can still find the unit that began it, to act once that unit commits.</summary>
    [Fact]
    public async Task The_unit_that_began_a_read_transaction_is_found_from_it()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            uow.BeginRead();

            Assert.Same(uow, UnitOfWork.OwnerOf(uow.ReadTransaction()));
        }
    }

    /// <summary>
    /// A unit that ends with nothing wrong hands its connection back to the pool as it is. Only a connection left inside a
    /// transaction is closed instead, so a connection's own state, here a temporary table, is still there for the next unit.
    /// </summary>
    [Fact]
    public async Task A_unit_that_ends_cleanly_returns_its_connection_to_the_pool()
    {
        using var db = new JobsTestDatabase();
        const string CountMarker = "SELECT count(*) FROM sqlite_temp_master WHERE name = 'pool_marker'";
        var first = await UnitOfWork.OpenAsync(db.Database);
        await using (first.ConfigureAwait(false))
        {
            await first.ExecuteAsync("CREATE TEMP TABLE pool_marker (id INTEGER)");
            await first.CommitAsync();
        }

        var second = await UnitOfWork.OpenAsync(db.Database);
        await using (second.ConfigureAwait(false))
        {
            Assert.Equal(1L, await second.CountAsync(CountMarker));
        }
    }

    [Fact]
    public async Task Asking_for_the_read_transaction_before_beginning_one_is_refused()
    {
        using var db = new JobsTestDatabase();
        var uow = await UnitOfWork.OpenAsync(db.Database);
        await using (uow.ConfigureAwait(false))
        {
            Assert.Throws<InvalidOperationException>(uow.ReadTransaction);
        }
    }

    /// <summary>
    /// A one-second, no-wait write from a second connection: succeeds when nothing holds the write lock, fails
    /// fast (never the thirty-second retry) when something does.
    /// </summary>
    private static bool ForeignWriteSucceedsWithoutWaiting(JobsTestDatabase db)
    {
        using var connection = db.Database.Open();
        using var noWait = connection.CreateCommand();
        noWait.CommandText = "PRAGMA busy_timeout=0";
        noWait.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = InsertJob;
        command.Parameters.AddWithValue("@dedupe", $"foreign-{Guid.NewGuid():N}");
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
    }
}
