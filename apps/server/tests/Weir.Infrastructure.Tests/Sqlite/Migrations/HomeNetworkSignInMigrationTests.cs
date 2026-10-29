using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0051_home_network_sign_in.sql</c>: an install that already exists keeps every setting it has and has the home network
/// sign-in off, with nothing saved for it. The database starts at revision 50, the last schema before the change, and the current build
/// opens it.
/// </summary>
public sealed class HomeNetworkSignInMigrationTests : IDisposable
{
    private const int RevisionBeforeTheSetting = 50;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public HomeNetworkSignInMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= RevisionBeforeTheSetting).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == RevisionBeforeTheSetting).Revision}');" +
            "UPDATE suite_settings SET log_retention_days = 14;");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_existing_install_keeps_its_settings_and_has_nothing_saved_for_the_home_network()
    {
        var database = new SqliteDatabase(_path);
        Assert.Equal(SchemaStartupOutcome.Upgraded, new SchemaMigrator(database).EnsureAtHead());
        database.ClearPool();

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM suite_settings WHERE log_retention_days = 14"));
        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM home_network_sign_in"));
    }

    [Fact]
    public void The_table_is_limited_to_one_row_that_starts_off_with_no_networks()
    {
        var database = new SqliteDatabase(_path);
        new SchemaMigrator(database).EnsureAtHead();
        database.ClearPool();
        SchemaSnapshot.Execute(_path, "INSERT INTO home_network_sign_in (id) VALUES (1)");

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM home_network_sign_in WHERE id = 1 AND enabled = 0 AND extra_networks = '' AND allow_list = ''"));
        var ddl = SchemaSnapshot.DescribeSchema(_path);
        Assert.Contains("CHECK (id = 1)", ddl, StringComparison.Ordinal);
    }
}
