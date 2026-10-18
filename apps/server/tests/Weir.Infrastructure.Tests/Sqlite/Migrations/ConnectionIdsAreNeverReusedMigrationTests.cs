using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0049_connection_ids_are_never_reused.sql</c>: a media manager connection's number is never given to another, so
/// what is told about a connection that was removed cannot land on the one that took its place. The database starts at revision 48,
/// the last schema before it, with connections, a link and a hand-off in it, and the current build opens it.
/// </summary>
public sealed class ConnectionIdsAreNeverReusedMigrationTests : IDisposable
{
    private const int RevisionBeforeTheChange = 48;

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public ConnectionIdsAreNeverReusedMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var oldMigrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= RevisionBeforeTheChange).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(oldMigrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(
            _path,
            $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{SchemaMigrator.Migrations.Single(migration => migration.Number == RevisionBeforeTheChange).Revision}');" +
            "INSERT INTO media_manager_connections (id, kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext, last_connection_test_ok, " +
            "last_connection_test_at, last_connection_test_detail, created_at, updated_at, downloaded_scan_enabled, nickname, last_answer_ms, last_used_at) VALUES " +
            "(7, 'deluno', 'Deluno on nas', 1, 'http://192.0.2.30:5000', 'key-7', 'secret-7', 1, '2026-10-01 01:00:00', 'Connected.', '2026-09-01 00:00:00', '2026-10-01 01:00:00', 0, '4K', 42, '2026-10-01 01:00:01')," +
            "(8, 'radarr', 'Radarr on nas', 0, 'http://192.0.2.30:7878', NULL, NULL, NULL, NULL, NULL, '2026-09-02 00:00:00', '2026-09-02 00:00:00', 1, NULL, NULL, NULL);" +
            "INSERT INTO library_manager_links (id, library_id, connection_id) VALUES (41, 1, 7);" +
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

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False;Foreign Keys=True");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void The_upgrade_keeps_every_connection_with_every_value_and_what_points_at_them()
    {
        Assert.Equal(SchemaStartupOutcome.Upgraded, Upgrade());

        Assert.Equal(
            1,
            SchemaSnapshot.ScalarLong(
                _path,
                "SELECT count(*) FROM media_manager_connections WHERE id = 7 AND kind = 'deluno' AND name = 'Deluno on nas' AND enabled = 1 " +
                "AND base_url = 'http://192.0.2.30:5000' AND api_key_ciphertext = 'key-7' AND webhook_secret_ciphertext = 'secret-7' " +
                "AND last_connection_test_ok = 1 AND last_connection_test_at = '2026-10-01 01:00:00' AND last_connection_test_detail = 'Connected.' " +
                "AND created_at = '2026-09-01 00:00:00' AND updated_at = '2026-10-01 01:00:00' AND downloaded_scan_enabled = 0 AND nickname = '4K' " +
                "AND last_answer_ms = 42 AND last_used_at = '2026-10-01 01:00:01'"));
        Assert.Equal(
            1,
            SchemaSnapshot.ScalarLong(
                _path,
                "SELECT count(*) FROM media_manager_connections WHERE id = 8 AND enabled = 0 AND downloaded_scan_enabled = 1 AND nickname IS NULL AND last_used_at IS NULL"));
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM library_manager_links WHERE id = 41 AND connection_id = 7"));
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'h1' AND connection_id = 7"));
        Assert.Equal(0, SchemaSnapshot.ScalarLong(_path, "SELECT count(*) FROM pragma_foreign_key_check"));
        Assert.Equal(1, SchemaSnapshot.ScalarLong(_path, $"SELECT count(*) FROM schema_version WHERE revision = '{SchemaMigrator.HeadRevision}'"));
    }

    [Fact]
    public void Before_the_upgrade_the_number_of_the_last_connection_removed_was_given_to_the_next_which_is_what_this_migration_ends()
    {
        Execute("DELETE FROM media_manager_connections WHERE id = 8");
        Execute("INSERT INTO media_manager_connections (kind, name) VALUES ('sonarr', 'Sonarr on nas')");

        Assert.Equal(8, SchemaSnapshot.ScalarLong(_path, "SELECT id FROM media_manager_connections WHERE name = 'Sonarr on nas'"));
    }

    [Fact]
    public void After_the_upgrade_the_number_of_a_removed_connection_is_not_given_to_the_next()
    {
        Upgrade();

        Execute("DELETE FROM media_manager_connections WHERE id = 8");
        Execute("INSERT INTO media_manager_connections (kind, name) VALUES ('sonarr', 'Sonarr on nas')");

        Assert.Equal(9, SchemaSnapshot.ScalarLong(_path, "SELECT id FROM media_manager_connections WHERE name = 'Sonarr on nas'"));
    }

    [Fact]
    public void After_the_upgrade_a_name_is_still_unique_and_a_connection_with_a_link_still_cannot_be_deleted()
    {
        Upgrade();

        var duplicate = Assert.Throws<SqliteException>(() => Execute("INSERT INTO media_manager_connections (kind, name) VALUES ('radarr', 'Radarr on nas')"));
        var refused = Assert.Throws<SqliteException>(() => Execute("DELETE FROM media_manager_connections WHERE id = 7"));

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
    }
}
