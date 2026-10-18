using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0048_backup_made_by.sql</c>: a backup made before Weir kept who made it keeps every other fact and says nothing about
/// who made it. The database starts at revision 47, the last schema before the column, with a backup in it, and the current build opens it.
/// </summary>
public sealed class BackupMadeByMigrationTests : IDisposable
{
    private const int RevisionBeforeTheColumn = 47;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public BackupMadeByMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= RevisionBeforeTheColumn).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == RevisionBeforeTheColumn).Revision}');" +
            "INSERT INTO suite_configuration_backup (id, created_at, file_name, size_bytes) VALUES (5, '2026-10-01 02:00:00.000000', 'suite-configuration-20261001-020000.json', 2048);");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_backup_made_before_the_column_is_kept_and_does_not_say_who_made_it()
    {
        var database = new SqliteDatabase(_path);
        Assert.Equal(SchemaStartupOutcome.Upgraded, new SchemaMigrator(database).EnsureAtHead());
        database.ClearPool();

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM suite_configuration_backup WHERE id = 5 AND size_bytes = 2048 AND made_by IS NULL"));
    }
}
