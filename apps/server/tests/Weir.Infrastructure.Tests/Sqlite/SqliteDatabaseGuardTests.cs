using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// What <see cref="SqliteDatabase"/> refuses to start, what it hands back when an open fails, and what it puts back after a
/// write transaction that waited for a writer outside Weir, each against a real SQLite file.
/// </summary>
public sealed class SqliteDatabaseGuardTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DatabaseFile => _temp.Join("weir.sqlite3");

    private static string PragmaText(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task Opening_with_the_token_already_cancelled_touches_no_file()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.OpenAsync(cancelled.Token));

            Assert.False(File.Exists(DatabaseFile), "a cancelled open still created the database file");
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_turn_at_the_write_gate_asked_for_with_the_token_already_cancelled_is_not_given()
    {
        var database = new SqliteDatabase(DatabaseFile);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.AcquireWriteGateAsync(cancelled.Token));

        var turn = database.AcquireWriteGateAsync(CancellationToken.None);
        Assert.True(turn.IsCompletedSuccessfully, "the refused turn is still held");
        Assert.Equal(TimeSpan.Zero, await turn);
        database.ReleaseWriteGate();
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_refused_and_no_connection_comes_back()
    {
        await File.WriteAllBytesAsync(DatabaseFile, Enumerable.Repeat((byte)'x', 4096).ToArray());
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var asynchronous = await Assert.ThrowsAsync<SqliteException>(() => database.OpenAsync());
            var synchronous = Assert.Throws<SqliteException>(database.Open);

            Assert.Equal(26, asynchronous.SqliteErrorCode);
            Assert.Equal(26, synchronous.SqliteErrorCode);
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_refusal_after_waiting_for_an_outside_writer_says_how_long_in_all_and_puts_the_connection_back_as_it_was()
    {
        var time = new SteppingTimeProvider(TimeSpan.FromSeconds(1));
        var database = new SqliteDatabase(DatabaseFile, busyTimeoutMilliseconds: 4000, timeProvider: time);
        try
        {
            await using var connection = await database.OpenAsync();
            await using var outsider = new SqliteConnection(database.ConnectionString);
            await outsider.OpenAsync();
            await using (var begin = outsider.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAsync<DatabaseBusyException>(
                () => database.BeginWriteTransactionAsync(connection, TimeSpan.FromMilliseconds(2500), CancellationToken.None)).WaitAsync(Limit);

            // The 2.5 s at the gate and the one second that database's clock saw pass while it waited for the outside writer.
            Assert.Equal(TimeSpan.FromMilliseconds(3500), refused.Waited);
            Assert.Equal(4, connection.DefaultTimeout);
            Assert.Equal("4000", PragmaText(connection, "busy_timeout"));

            await using var rollback = outsider.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
        finally
        {
            database.ClearPool();
        }
    }

    /// <summary>A clock that moves a fixed step every time it is read, so the time a wait took is known without waiting for it.</summary>
    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private const long Frequency = 1000;
        private long _ticks;

        public override long TimestampFrequency => Frequency;

        public override long GetTimestamp() => _ticks += (long)(step.TotalSeconds * Frequency);
    }
}
