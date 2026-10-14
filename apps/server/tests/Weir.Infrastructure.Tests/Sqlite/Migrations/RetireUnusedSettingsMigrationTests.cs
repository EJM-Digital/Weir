using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0046_retire_unused_settings.sql</c>: a connection's search lanes, the signed-in home note and the hours window for
/// Movies and for TV are gone from a database that kept them, and nothing else a person set is touched.
/// <para>Each test builds a database as Weir 1.0.0-rc.18 left it, saves values in the settings this migration retires, and opens
/// it the way a start of the next version does.</para>
/// </summary>
public sealed class RetireUnusedSettingsMigrationTests : IDisposable
{
    /// <summary>The newest migration in Weir 1.0.0-rc.18, the last release before this one.</summary>
    private const int LastRelease = 45;

    private static readonly string[] RetiredOperatorColumns =
    [
        "movie_schedule_hours_limited", "movie_schedule_days", "movie_schedule_start", "movie_schedule_end",
        "tv_schedule_hours_limited", "tv_schedule_days", "tv_schedule_start", "tv_schedule_end",
    ];

    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public RetireUnusedSettingsMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var migrations = SchemaMigrator.Migrations.Where(migration => migration.Number <= LastRelease).OrderBy(migration => migration.Number).ToList();
        SchemaSnapshot.Execute(_path, string.Concat(migrations.Select(SchemaMigrator.ReadMigrationSql)));
        SchemaSnapshot.Execute(_path, $"DELETE FROM schema_version; INSERT INTO schema_version (revision) VALUES ('{migrations[^1].Revision}');");
        SaveSettingsThatDoNothing();
    }

    public void Dispose() => _temp.Dispose();

    private void SaveSettingsThatDoNothing()
    {
        SchemaSnapshot.Execute(
            _path,
            "INSERT INTO media_manager_connections (id, kind, name, base_url, enabled) VALUES (7, 'deluno', 'Deluno on 192.0.2.30', 'http://192.0.2.30:5099', 1);" +
            "INSERT INTO media_manager_search_lanes (connection_id, lane, enabled, max_items_per_run, retry_delay_minutes, schedule_enabled, schedule_days, " +
            "schedule_start, schedule_end, schedule_interval_seconds) VALUES (7, 'missing', 1, 25, 60, 1, 'Mon,Tue', '01:00', '05:00', 900);" +
            "INSERT INTO media_manager_search_lanes (connection_id, lane) VALUES (7, 'upgrade');" +
            "UPDATE suite_settings SET signed_in_home_notice = 'Welcome back.', app_timezone = 'Europe/London', log_retention_days = 45 WHERE id = 1;" +
            "UPDATE operator_settings SET movie_schedule_enabled = 0, movie_schedule_hours_limited = 1, movie_schedule_days = 'Mon,Tue', " +
            "movie_schedule_start = '09:00', movie_schedule_end = '17:30', tv_schedule_enabled = 1, tv_schedule_hours_limited = 1, " +
            "tv_schedule_days = 'Wed', tv_schedule_start = '08:00', tv_schedule_end = '09:30', max_concurrent_files = 4 WHERE id = 1;");
    }

    private SchemaStartupOutcome Open()
    {
        var database = new SqliteDatabase(_path);
        var outcome = new SchemaMigrator(database).EnsureAtHead();
        database.ClearPool();
        return outcome;
    }

    private long Count(string sql) => SchemaSnapshot.ScalarLong(_path, sql);

    [Fact]
    public void A_database_from_the_last_release_is_upgraded_and_opens_at_head()
    {
        Assert.Equal(SchemaStartupOutcome.Upgraded, Open());

        Assert.Equal(1, Count($"SELECT COUNT(*) FROM schema_version WHERE revision = '{SchemaMigrator.HeadRevision}'"));
    }

    [Fact]
    public void A_connection_no_longer_has_search_lanes()
    {
        Open();

        Assert.Equal(0, Count("SELECT COUNT(*) FROM sqlite_master WHERE name = 'media_manager_search_lanes'"));
    }

    [Fact]
    public void A_connection_that_had_lanes_keeps_everything_else_and_can_still_be_removed()
    {
        Open();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM media_manager_connections WHERE id = 7 AND kind = 'deluno' AND base_url = 'http://192.0.2.30:5099' AND enabled = 1"));
        SchemaSnapshot.Execute(_path, "DELETE FROM media_manager_connections WHERE id = 7");
        Assert.Equal(0, Count("SELECT COUNT(*) FROM media_manager_connections"));
    }

    [Fact]
    public void Suite_settings_no_longer_hold_a_home_note_and_keep_the_rest()
    {
        Open();

        Assert.Equal(0, Count("SELECT COUNT(*) FROM pragma_table_info('suite_settings') WHERE name = 'signed_in_home_notice'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM suite_settings WHERE id = 1 AND app_timezone = 'Europe/London' AND log_retention_days = 45"));
    }

    [Fact]
    public void Performance_no_longer_holds_an_hours_window_and_keeps_the_rest()
    {
        Open();

        var kept = Count(
            $"SELECT COUNT(*) FROM pragma_table_info('operator_settings') WHERE name IN ({string.Join(", ", RetiredOperatorColumns.Select(name => $"'{name}'"))})");
        Assert.Equal(0, kept);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM operator_settings WHERE id = 1 AND movie_schedule_enabled = 0 AND tv_schedule_enabled = 1 AND max_concurrent_files = 4"));
    }

    [Fact]
    public void A_database_with_no_saved_values_in_them_upgrades_the_same_way()
    {
        SchemaSnapshot.Execute(_path, "DELETE FROM media_manager_search_lanes; DELETE FROM media_manager_connections; UPDATE suite_settings SET signed_in_home_notice = NULL;");

        Assert.Equal(SchemaStartupOutcome.Upgraded, Open());
        Assert.Equal(0, Count("SELECT COUNT(*) FROM media_manager_connections"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM suite_settings WHERE id = 1"));
    }
}
