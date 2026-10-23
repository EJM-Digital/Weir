using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// The settings every connection is opened with, the limits of the write gate and what a unit of work tells its caller,
/// each against a real SQLite file.
/// </summary>
public sealed class SqliteWriteGateDetailTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DatabaseFile => _temp.Join("weir.sqlite3");

    private static long Pragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string PragmaText(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_connection_is_opened_in_write_ahead_mode_with_foreign_keys_on_and_the_configured_busy_timeout()
    {
        var database = new SqliteDatabase(DatabaseFile, busyTimeoutMilliseconds: 4321);
        try
        {
            var asynchronous = await database.OpenAsync();
            await using (asynchronous.ConfigureAwait(false))
            {
                Assert.Equal("wal", PragmaText(asynchronous, "journal_mode"));
                Assert.Equal(1, Pragma(asynchronous, "foreign_keys"));
                Assert.Equal(4321, Pragma(asynchronous, "busy_timeout"));
                Assert.Equal(1, Pragma(asynchronous, "synchronous"));
            }

            using var synchronous = database.Open();
            Assert.Equal("wal", PragmaText(synchronous, "journal_mode"));
            Assert.Equal(1, Pragma(synchronous, "foreign_keys"));
            Assert.Equal(4321, Pragma(synchronous, "busy_timeout"));
            Assert.Equal(1, Pragma(synchronous, "synchronous"));
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Theory]
    [InlineData(30000, 30)]
    [InlineData(7000, 7)]
    [InlineData(2500, 2)]
    public void A_command_waits_for_the_busy_timeout_counted_in_whole_seconds(int busyTimeoutMilliseconds, int seconds)
    {
        var database = new SqliteDatabase(DatabaseFile, busyTimeoutMilliseconds: busyTimeoutMilliseconds);

        using var connection = new SqliteConnection(database.ConnectionString);

        Assert.Equal(seconds, connection.DefaultTimeout);
    }

    [Theory]
    [InlineData(30000, 0, 30)]
    [InlineData(30000, 29500, 1)]
    [InlineData(30000, 40000, 1)]
    [InlineData(5000, 1500, 4)]
    [InlineData(1500, 0, 2)]
    [InlineData(1000, 0, 1)]
    [InlineData(1, 0, 1)]
    public void What_is_left_of_the_busy_timeout_after_the_gate_is_rounded_up_to_a_whole_second_and_never_below_one(int busyTimeoutMilliseconds, int waitedMilliseconds, int seconds)
    {
        Assert.Equal(seconds, SqliteDatabase.CommandTimeoutSeconds(busyTimeoutMilliseconds, TimeSpan.FromMilliseconds(waitedMilliseconds)));
    }

    [Fact]
    public void Letting_go_of_a_database_opened_without_pooling_needs_no_gate()
    {
        var database = new SqliteDatabase(DatabaseFile, pooling: false);

        var exception = Record.Exception(database.ClearPool);

        Assert.Null(exception);
    }

    [Fact]
    public async Task A_database_file_that_has_gone_is_refused_once_by_name_and_the_next_open_starts_afresh()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            using (database.Open())
            {
            }

            database.ClearPool();
            File.Delete(DatabaseFile);

            var refused = await Assert.ThrowsAsync<SqliteException>(() => database.OpenAsync());

            Assert.Equal("unable to open database file", refused.Message);
            Assert.Equal(14, refused.SqliteErrorCode);
            Assert.False(File.Exists(DatabaseFile));

            var fresh = await database.OpenAsync();
            await fresh.DisposeAsync();
            Assert.True(File.Exists(DatabaseFile));
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_connection_handed_out_still_inside_a_transaction_is_rolled_back_and_the_log_says_so()
    {
        var logger = new ListLogger<SqliteDatabase>();
        var database = new SqliteDatabase(DatabaseFile, logger: logger);
        try
        {
            var left = await database.OpenAsync();
            Run(left, "CREATE TABLE notes (text TEXT)");
            Run(left, "BEGIN IMMEDIATE");
            Run(left, "INSERT INTO notes VALUES ('never committed')");
            await left.DisposeAsync();

            var next = await database.OpenAsync();
            await using (next.ConfigureAwait(false))
            {
                Assert.Equal(1, raw.sqlite3_get_autocommit(next.Handle));
                using var count = next.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM notes";
                Assert.Equal(0L, count.ExecuteScalar());
            }

            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains("handed out still inside a transaction; Weir rolled it back before using the connection.", StringComparison.Ordinal));
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_connection_handed_out_inside_a_transaction_that_cannot_be_rolled_back_is_closed_and_a_new_one_is_opened()
    {
        var database = new SqliteDatabase(DatabaseFile, logger: new ListLogger<SqliteDatabase>());
        try
        {
            var left = await database.OpenAsync();
            Run(left, "CREATE TABLE notes (text TEXT)");
            Run(left, "BEGIN IMMEDIATE");
            Run(left, "INSERT INTO notes VALUES ('never committed')");
            var abandoned = left.Handle;
            Assert.Equal(raw.SQLITE_OK, raw.sqlite3_set_authorizer(abandoned, RefuseRollback, null));
            await left.DisposeAsync();

            var next = await database.OpenAsync();
            await using (next.ConfigureAwait(false))
            {
                Assert.NotSame(abandoned, next.Handle);
                Assert.Equal(1, raw.sqlite3_get_autocommit(next.Handle));
                using var count = next.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM notes";
                Assert.Equal(0L, count.ExecuteScalar());
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    private static int RefuseRollback(object? state, int action, utf8z first, utf8z second, utf8z database, utf8z trigger) =>
        action == raw.SQLITE_TRANSACTION && first.utf8_to_string() == "ROLLBACK" ? raw.SQLITE_DENY : raw.SQLITE_OK;

    [Fact]
    public async Task A_refusal_from_sqlite_that_is_not_about_a_busy_database_is_passed_on_as_it_is()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var connection = await database.OpenAsync();
            await using (connection.ConfigureAwait(false))
            {
                Run(connection, "BEGIN");

                var refused = await Assert.ThrowsAsync<SqliteException>(() => database.BeginWriteTransactionAsync(connection, TimeSpan.Zero, CancellationToken.None));

                Assert.IsNotType<DatabaseBusyException>(refused);
                Assert.Contains("within a transaction", refused.Message, StringComparison.Ordinal);
                Run(connection, "ROLLBACK");
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_unit_of_work_whose_transaction_cannot_begin_gives_its_turn_at_the_write_lock_back()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var unit = await UnitOfWork.OpenAsync(database);
            await using (unit.ConfigureAwait(false))
            {
                Run(unit.Connection, "BEGIN");

                await Assert.ThrowsAsync<SqliteException>(() => unit.ExecuteAsync("CREATE TABLE notes (text TEXT)"));

                var turn = database.AcquireWriteGateAsync(CancellationToken.None);
                Assert.True(turn.IsCompletedSuccessfully, "the failed unit of work still holds its turn at the write lock");
                Assert.Equal(TimeSpan.Zero, await turn);
                database.ReleaseWriteGate();
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task Disposing_a_unit_of_work_closes_its_connection()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var unit = await UnitOfWork.OpenAsync(database);
            var connection = unit.Connection;
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);

            await unit.DisposeAsync();

            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_second_transaction_cannot_be_begun_and_the_error_says_one_is_open()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var unit = await UnitOfWork.OpenAsync(database);
            await using (unit.ConfigureAwait(false))
            {
                await unit.BeginImmediateAsync();

                var again = await Assert.ThrowsAsync<InvalidOperationException>(unit.BeginImmediateAsync);
                var read = Assert.Throws<InvalidOperationException>(unit.BeginRead);

                Assert.Equal("A transaction is already open.", again.Message);
                Assert.Equal("A transaction is already open.", read.Message);
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_read_transaction_asked_for_before_it_was_begun_says_to_begin_it_first()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var unit = await UnitOfWork.OpenAsync(database);
            await using (unit.ConfigureAwait(false))
            {
                var error = Assert.Throws<InvalidOperationException>(unit.ReadTransaction);

                Assert.Equal("Call BeginRead() first.", error.Message);
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    private static SqliteDataReader ReadRow(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return reader;
    }

    [Fact]
    public void Stored_values_read_back_as_the_caller_expects_and_nothing_stored_reads_as_empty_or_false()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var reader = ReadRow(connection, "SELECT NULL, NULL, 'x', '2026-03-04T05:06:07+00:00', 'not a time'");

        Assert.Equal(string.Empty, SqliteValues.GetString(reader, 0));
        Assert.False(SqliteValues.GetBool(reader, 1));
        Assert.Equal("x", SqliteValues.GetString(reader, 2));
        Assert.NotNull(SqliteValues.GetDateTimeOrNull(reader, 3));
        Assert.Null(SqliteValues.GetStringOrNull(reader, 0));
        Assert.Null(SqliteValues.GetDateTimeOrNull(reader, 0));
    }

    [Fact]
    public void A_stored_time_that_cannot_be_read_names_the_text_and_a_missing_required_time_says_so()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var reader = ReadRow(connection, "SELECT NULL, 'not a time'");

        var unreadable = Assert.Throws<FormatException>(() => SqliteValues.GetDateTimeOrNull(reader, 1));
        var missing = Assert.Throws<FormatException>(() => SqliteValues.GetDateTime(reader, 0));

        Assert.Equal("Invalid isoformat string: 'not a time'", unreadable.Message);
        Assert.Equal("A required timestamp column is NULL.", missing.Message);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(0, 10)]
    [InlineData(2, 10)]
    [InlineData(3, 10)]
    [InlineData(4, 12)]
    [InlineData(100, 300)]
    [InlineData(333, 999)]
    [InlineData(334, 1000)]
    [InlineData(5000, 1000)]
    public void A_bulk_job_stands_back_three_times_as_long_as_it_held_the_lock_within_its_limits(int heldMilliseconds, int pauseMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(pauseMilliseconds), WriteLockTurns.PauseAfter(TimeSpan.FromMilliseconds(heldMilliseconds)));
    }

    [Fact]
    public async Task A_bulk_job_runs_its_transaction_before_it_stands_back_and_stops_standing_back_when_cancelled()
    {
        var ran = false;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteLockTurns.TakeAsync(
            () =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            cancelled.Token));

        Assert.True(ran);
    }
}
