using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>SQLite reports progress from inside a statement that is working, and is silent while one waits for a lock.</summary>
public sealed class SqliteWorkProgressTests
{
    // Counts to a few million with a recursive query: far more instructions than one report's worth.
    private const string LongStatement = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000000) SELECT COUNT(*) FROM n";

    [Fact]
    public void A_statement_that_is_working_reports_again_and_again()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var reports = 0;

        using (SqliteWorkProgress.Report(connection, () => reports++))
        {
            using var command = connection.CreateCommand();
            command.CommandText = LongStatement;
            Assert.Equal(3_000_000L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.True(reports > 100, $"Only {reports} reports from a statement of millions of instructions");
    }

    [Fact]
    public void Nothing_is_reported_once_the_report_is_disposed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var reports = 0;
        SqliteWorkProgress.Report(connection, () => reports++).Dispose();

        using var command = connection.CreateCommand();
        command.CommandText = LongStatement;
        command.ExecuteScalar();

        Assert.Equal(0, reports);
    }

    [Fact]
    public void A_statement_waiting_for_a_lock_reports_nothing_because_it_is_not_working()
    {
        using var temp = new TempDirectory();
        var path = temp.Join("locked.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString();
        using var holder = new SqliteConnection(connectionString);
        holder.Open();
        Exec(holder, "CREATE TABLE t (x INTEGER); BEGIN IMMEDIATE;");

        using var waiter = new SqliteConnection(connectionString);
        waiter.Open();
        var reports = 0;
        using (SqliteWorkProgress.Report(waiter, () => reports++))
        {
            // The holder keeps the write lock, so this waits out the one-second busy timeout and gives up.
            Assert.Throws<SqliteException>(() => Exec(waiter, "BEGIN IMMEDIATE"));
        }

        Assert.Equal(0, reports);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
