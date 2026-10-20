using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0050_backup_password.sql</c>: an install that already backs up keeps every backup it has, none of them marked as holding
/// sealed secrets, and has no backup password until the person sets one. The database starts at revision 49, the last schema before the
/// change, with a backup in it, and the current build opens it.
/// </summary>
public sealed class BackupPasswordMigrationTests : IDisposable
{
    private const int RevisionBeforeThePassword = 49;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public BackupPasswordMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= RevisionBeforeThePassword).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == RevisionBeforeThePassword).Revision}');" +
            "UPDATE suite_settings SET configuration_backup_enabled = 1;" +
            "INSERT INTO suite_configuration_backup (id, created_at, file_name, size_bytes, made_by) VALUES (5, '2026-10-01 02:00:00.000000', 'suite-configuration-20261001-020000.json', 2048, 'schedule');");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_install_that_already_backs_up_keeps_its_backups_unsealed_and_has_no_password()
    {
        var database = new SqliteDatabase(_path);
        Assert.Equal(SchemaStartupOutcome.Upgraded, new SchemaMigrator(database).EnsureAtHead());
        database.ClearPool();

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM suite_configuration_backup WHERE id = 5 AND size_bytes = 2048 AND made_by = 'schedule' AND secrets_sealed = 0"));
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM suite_settings WHERE configuration_backup_enabled = 1"));
        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM backup_password"));
    }
}
