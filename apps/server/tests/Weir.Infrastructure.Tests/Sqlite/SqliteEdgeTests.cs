using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>The corners of the SQLite layer: values stored in an unexpected type, migrations that go wrong, handles that were never opened.</summary>
public sealed class SqliteEdgeTests
{
    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Theory]
    [InlineData("7", 7L)]
    [InlineData("7.9", 7L)]
    [InlineData("' 12 '", 12L)]
    [InlineData("'twelve'", 0L)]
    [InlineData("x'00'", 0L)]
    [InlineData("NULL", null)]
    public void A_stored_number_reads_as_a_whole_number_whatever_type_it_was_stored_in(string literal, long? expected)
    {
        using var connection = OpenMemory();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {literal}";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(expected, SqliteValues.GetInt64OrNull(reader, 0));
    }

    [Fact]
    public void A_transaction_no_unit_of_work_began_has_no_owner()
    {
        using var connection = OpenMemory();
        using var transaction = connection.BeginTransaction();

        Assert.Null(UnitOfWork.OwnerOf(transaction));
    }

    [Fact]
    public async Task A_count_of_no_rows_is_zero()
    {
        using var temp = new TempDirectory();
        var database = new SqliteDatabase(temp.Join("weir.sqlite3"));
        new SchemaMigrator(database).EnsureAtHead();
        var uow = await UnitOfWork.OpenAsync(database);
        await using (uow)
        {
            Assert.Equal(0L, await uow.CountAsync("SELECT 1 WHERE 0"));
        }

        database.ClearPool();
    }

    [Fact]
    public void A_connection_that_was_never_opened_cannot_report_progress()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        Assert.Throws<InvalidOperationException>(() => SqliteWorkProgress.Report(connection, () => { }));
    }

    [Fact]
    public void A_migration_whose_file_is_missing_from_the_build_is_refused_by_name()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrator.ReadMigrationSql(new SchemaMigration(9999, "9999_missing", "Weir.Infrastructure.Sqlite.Migrations.9999_missing.sql")));

        Assert.Contains("9999_missing.sql", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_that_leave_no_version_table_are_refused()
    {
        using var connection = OpenMemory();
        var step = new SchemaMigration(1, "0001_one", "one");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrator.ApplyRange(connection, [step], "0001_one", readSql: _ => "CREATE TABLE parent (id INTEGER PRIMARY KEY);"));

        Assert.Equal("No schema version table exists after the migrations ran.", failure.Message);
    }

    [Fact]
    public void Migrations_that_leave_a_row_pointing_at_nothing_are_refused_before_they_commit()
    {
        using var connection = OpenMemory();
        var step = new SchemaMigration(1, "0001_one", "one");
        const string sql =
            "CREATE TABLE schema_version (revision TEXT NOT NULL);" +
            "CREATE TABLE parent (id INTEGER PRIMARY KEY);" +
            "CREATE TABLE child (parent_id INTEGER REFERENCES parent (id));" +
            "INSERT INTO child (parent_id) VALUES (7);";

        var failure = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrator.ApplyRange(connection, [step], "0001_one", readSql: _ => sql));

        Assert.Equal("Migration to 0001_one left a foreign key violation in table 'child'.", failure.Message);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'child'";
        Assert.Equal(0L, Convert.ToInt64(tables.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_schema_mismatch_can_be_made_with_a_message_a_cause_or_neither()
    {
        var cause = new InvalidOperationException("cause");

        Assert.Equal(SchemaMismatchKind.Incompatible, new DatabaseSchemaMismatchException("message", SchemaMismatchKind.Incompatible).Kind);
        Assert.Same(cause, new DatabaseSchemaMismatchException("message", cause).InnerException);
        Assert.NotEmpty(new DatabaseSchemaMismatchException().Message);
    }
}
