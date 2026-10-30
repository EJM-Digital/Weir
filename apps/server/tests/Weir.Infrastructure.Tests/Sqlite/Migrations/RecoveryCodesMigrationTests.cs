using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0052_recovery_codes.sql</c>: an account that already exists keeps working and has no recovery codes until it makes a set.
/// The database starts at revision 50, the last schema before the change, with an account in it, and the current build opens it.
/// </summary>
public sealed class RecoveryCodesMigrationTests : IDisposable
{
    private const int RevisionBeforeTheCodes = 50;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public RecoveryCodesMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= RevisionBeforeTheCodes).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == RevisionBeforeTheCodes).Revision}');" +
            "INSERT INTO users (id, username, password_hash, role, is_active) VALUES (3, 'alice', 'hash', 'admin', 1);");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_existing_account_is_kept_and_has_no_recovery_codes()
    {
        var database = new SqliteDatabase(_path);
        Assert.Equal(SchemaStartupOutcome.Upgraded, new SchemaMigrator(database).EnsureAtHead());
        database.ClearPool();

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM users WHERE id = 3 AND username = 'alice'"));
        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM recovery_codes"));
    }

    [Fact]
    public void Deleting_the_account_deletes_its_codes()
    {
        var database = new SqliteDatabase(_path);
        new SchemaMigrator(database).EnsureAtHead();
        database.ClearPool();
        SchemaSnapshot.Execute(_path, "INSERT INTO recovery_codes (user_id, lookup, code_hash) VALUES (3, 'ABCDE', 'hash');");
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM recovery_codes"));

        SchemaSnapshot.Execute(_path, "PRAGMA foreign_keys = ON; DELETE FROM users WHERE id = 3;");

        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM recovery_codes"));
    }
}
