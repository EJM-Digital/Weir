using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// A person upgrading from last week's release is the case that matters. Each fixture is the database a published build wrote after a short
/// scripted session (an account, a rule set, workflows, one file finished and one refused, a scanned library, an alert), unpacked to the
/// place Weir keeps it and opened with the current build the way a start opens it: a copy is saved first, then the migrations run.
/// </summary>
public sealed class ReleaseDatabaseUpgradeTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));

    public static TheoryData<string> Versions() => ReleaseDatabases.Versions();

    public void Dispose() => _home.Dispose();

    private string DatabasePath => _home.Join("data", "weir.sqlite3");

    private string BackupDir => _home.Join("backups");

    /// <summary>Opens the unpacked fixture as a start does: saves a copy of the old database, then migrates.</summary>
    private SchemaStartupOutcome Start(string version)
    {
        var database = new SqliteDatabase(DatabasePath);
        var backup = new PreUpdateBackup(BackupDir, _home.Path, "9.9.9", _time, new ListLogger<PreUpdateBackup>(), fromVersion: version);
        try
        {
            return new SchemaMigrator(database).EnsureAtHead((connection, revision) => backup.Save(connection, revision));
        }
        finally
        {
            database.ClearPool();
        }
    }

    private long Count(string table) => SchemaSnapshot.ScalarLong(DatabasePath, $"SELECT COUNT(*) FROM \"{table}\"");

    private bool Exists(string table) => SchemaSnapshot.ScalarLong(DatabasePath, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'") > 0;

    private string Text(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return string.Join(" | ", ReadAll(command));
    }

    private static List<string> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_fixture_is_the_file_the_manifest_describes(string version)
    {
        var release = ReleaseDatabases.Of(version);
        ReleaseDatabases.Unpack(version, DatabasePath);

        Assert.Equal(release.Sha256, ReleaseDatabases.Sha256Of(version));
        Assert.Equal(release.SchemaRevision, Text("SELECT revision FROM schema_version"));
        Assert.Matches("^sha256:[0-9a-f]{64}$", release.ImageDigest);
        Assert.NotEqual(SchemaMigrator.HeadRevision, release.SchemaRevision);
        foreach (var (table, rows) in release.Rows)
        {
            Assert.Equal(rows, Count(table));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_database_written_by_a_published_release_upgrades_to_the_current_schema(string version)
    {
        ReleaseDatabases.Unpack(version, DatabasePath);

        var outcome = Start(version);

        Assert.Equal(SchemaStartupOutcome.Upgraded, outcome);
        Assert.Equal(SchemaMigrator.HeadRevision, Text("SELECT revision FROM schema_version"));
        Assert.Equal("ok", Text("PRAGMA integrity_check"));
        Assert.Equal(string.Empty, Text("PRAGMA foreign_key_check"));
        using var fresh = new TempDirectory();
        var freshPath = fresh.Join("fresh.sqlite3");
        var freshDatabase = new SqliteDatabase(freshPath);
        new SchemaMigrator(freshDatabase).EnsureAtHead();
        freshDatabase.ClearPool();
        Assert.Equal(SchemaSnapshot.DescribeSchema(freshPath), SchemaSnapshot.DescribeSchema(DatabasePath));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_copy_saved_before_the_upgrade_is_the_old_database(string version)
    {
        var release = ReleaseDatabases.Of(version);
        ReleaseDatabases.Unpack(version, DatabasePath);

        Start(version);

        var copy = PreUpdateBackup.Latest(BackupDir);
        Assert.NotNull(copy);
        Assert.Equal(version, copy.FromVersion);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT revision FROM schema_version";
        Assert.Equal(release.SchemaRevision, revision.ExecuteScalar());
        foreach (var (table, rows) in release.Rows)
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            Assert.Equal(rows, Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Everything_the_session_left_is_still_there_after_the_upgrade(string version)
    {
        var release = ReleaseDatabases.Of(version);
        ReleaseDatabases.Unpack(version, DatabasePath);

        Start(version);

        Assert.Equal("alice,admin,1", Text("SELECT username, role, is_active FROM users"));
        Assert.Equal("Fixture Library | Fixture Movies | Movies | TV", Text("SELECT group_concat(name, ' | ') FROM (SELECT name FROM libraries ORDER BY name)"));
        Assert.Equal("Fixture rules", Text("SELECT name FROM rule_sets WHERE name = 'Fixture rules'"));
        Assert.Equal("Fixture alert,webhook,1", Text("SELECT label, provider, enabled FROM notification_channels"));
        Assert.Equal("roku", Text("SELECT direct_play_devices FROM suite_settings"));
        Assert.Equal("deluno,1", Text("SELECT kind, enabled FROM media_manager_connections"));
        Assert.Equal("fixture-finished,completed | fixture-refused,rejected", Text("SELECT handoff_id, state FROM media_manager_handoffs ORDER BY handoff_id").Replace("\n", " | "));
        Assert.Equal(
            "Fixture.Film.2020/Fixture.Film.2020.mkv,processed | Fixture.Silent.2021/Fixture.Silent.2021.mkv,rejected",
            Text("SELECT relative_path, status FROM files ORDER BY relative_path"));
        Assert.Equal("imported", Text("SELECT outcome FROM handbacks"));
        Assert.Contains("Fixture.Film.2020.mkv was processed successfully", Text("SELECT title FROM activity_events"), StringComparison.Ordinal);
        Assert.Contains("Scanned Fixture Library", Text("SELECT title FROM activity_events"), StringComparison.Ordinal);
        foreach (var (table, rows) in release.Rows.Where(row => Exists(row.Key) && row.Key is not ("schema_version" or "jobs" or "server_starts" or "activity_events")))
        {
            Assert.True(Count(table) >= rows, $"{table} held {rows} rows before the upgrade and holds {Count(table)} now.");
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Opening_the_upgraded_database_again_changes_nothing(string version)
    {
        ReleaseDatabases.Unpack(version, DatabasePath);
        Start(version);
        var before = SchemaSnapshot.Describe(DatabasePath);
        var copies = Directory.EnumerateFiles(PreUpdateBackup.FolderIn(BackupDir)).Count();

        var outcome = Start(version);

        Assert.Equal(SchemaStartupOutcome.AlreadyCurrent, outcome);
        Assert.Equal(before, SchemaSnapshot.Describe(DatabasePath));
        Assert.Equal(copies, Directory.EnumerateFiles(PreUpdateBackup.FolderIn(BackupDir)).Count());
    }

    [Fact]
    public void Every_release_fixture_is_listed_and_every_listed_release_has_its_file()
    {
        var files = Directory.EnumerateFiles(ReleaseDatabases.Folder, "*.db.gz").Select(file => Path.GetFileName(file)[..^".db.gz".Length]).Order(StringComparer.Ordinal);

        Assert.Equal(ReleaseDatabases.All.Select(release => release.Version).Order(StringComparer.Ordinal), files);
        Assert.True(ReleaseDatabases.All.Count >= 6, "the fixtures start at 1.0.0-rc.10 and hold every published release since");
    }
}
