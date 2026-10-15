using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0047_connection_links_are_kept.sql</c>: a workflow's link to a media manager connection no longer disappears with the
/// connection. Each test opens a database at revision 45, the schema Weir 1.0.0-rc.18 created and the last release's (migrations 1 to
/// 45 are unchanged since), puts workflows, connections and links in it the way that release stored them, and starts the current
/// build over it.
/// </summary>
public sealed class ConnectionLinksAreKeptMigrationTests : IDisposable
{
    private const int LastReleaseRevision = 45;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public ConnectionLinksAreKeptMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= LastReleaseRevision).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == LastReleaseRevision).Revision}');" +
            "INSERT INTO media_manager_connections (id, kind, name, base_url) VALUES (7, 'deluno', 'Deluno on nas', 'http://192.0.2.30:5000'), (8, 'radarr', 'Radarr on nas', 'http://192.0.2.30:7878');" +
            "INSERT INTO library_manager_links (id, library_id, connection_id) VALUES (41, 1, 7), (42, 2, 7), (43, 1, 8);" +
            "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state, connection_id) VALUES ('deluno', 'h1', 1, 'Film/film.mkv', 'working', 7);");
    }

    public void Dispose() => _temp.Dispose();

    private SchemaStartupOutcome Upgrade()
    {
        var database = new SqliteDatabase(_path);
        var outcome = new SchemaMigrator(database).EnsureAtHead();
        database.ClearPool();
        return outcome;
    }

    private List<string> Rows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private void ExecuteWithForeignKeys(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False;Foreign Keys=True");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_last_release_database_is_upgraded_with_every_link_workflow_connection_and_hand_off_kept()
    {
        Assert.Equal(SchemaStartupOutcome.Upgraded, Upgrade());

        Assert.Equal(
            "41:1:7,42:2:7,43:1:8",
            string.Join(',', Rows("SELECT id || ':' || library_id || ':' || connection_id FROM library_manager_links ORDER BY id")));
        Assert.Equal(2, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM libraries"));
        Assert.Equal(2, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM media_manager_connections"));
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'h1' AND connection_id = 7"));
        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM pragma_foreign_key_check"));
        Assert.Equal(SchemaMigrator.HeadRevision, Rows("SELECT revision FROM schema_version").Single());
    }

    [Fact]
    public void After_the_upgrade_a_connection_with_a_link_cannot_be_deleted_and_nothing_cascades()
    {
        Upgrade();

        var refused = Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys("DELETE FROM media_manager_connections WHERE id = 7"));

        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
        Assert.Equal(3, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM library_manager_links"));
        Assert.Equal(2, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM media_manager_connections"));
    }

    [Fact]
    public void Before_the_upgrade_the_last_release_took_the_links_with_the_connection_which_is_what_this_migration_ends()
    {
        ExecuteWithForeignKeys("DELETE FROM media_manager_connections WHERE id = 7");

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM library_manager_links"));
    }

    [Fact]
    public void After_the_upgrade_a_connection_with_no_link_can_still_be_deleted_and_a_link_still_goes_with_its_workflow()
    {
        Upgrade();
        ExecuteWithForeignKeys("DELETE FROM library_manager_links WHERE connection_id = 8");
        ExecuteWithForeignKeys("DELETE FROM media_manager_connections WHERE id = 8");

        ExecuteWithForeignKeys("DELETE FROM libraries WHERE id = 2");

        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM media_manager_connections"));
        Assert.Equal("41:1:7", string.Join(',', Rows("SELECT id || ':' || library_id || ':' || connection_id FROM library_manager_links")));
    }

    [Fact]
    public void A_link_can_still_be_added_after_the_upgrade_and_the_pair_stays_unique()
    {
        Upgrade();

        ExecuteWithForeignKeys("INSERT INTO library_manager_links (library_id, connection_id) VALUES (2, 8)");
        var duplicate = Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys("INSERT INTO library_manager_links (library_id, connection_id) VALUES (2, 8)"));

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(4, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM library_manager_links"));
    }
}
