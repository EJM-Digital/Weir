using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The checks on the copy before an update goes ahead, and how the copies already taken are read back.</summary>
public sealed class PreUpdateBackupVerificationTests : IDisposable
{
    private const string Version = "1.2.3";

    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly ListLogger<PreUpdateBackup> _logger = new();

    public void Dispose() => _home.Dispose();

    private string BackupDir => _home.Join("backups");

    private string Folder => PreUpdateBackup.FolderIn(BackupDir);

    private PreUpdateBackup Backup(Action<SqliteConnection, string>? copy = null, Func<string, long?>? freeSpace = null)
    {
        var backup = new PreUpdateBackup(BackupDir, _home.Path, Version, _time, _logger);
        if (copy is not null)
        {
            backup.CopyDatabase = copy;
        }

        if (freeSpace is not null)
        {
            backup.FreeSpaceOf = freeSpace;
        }

        return backup;
    }

    private SqliteDatabase OldDatabase()
    {
        var path = _home.Join("data", "weir.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = new SqliteDatabase(path);
        new SchemaMigrator(database).EnsureAtBaseline();
        return database;
    }

    /// <summary>A copy as the real one is made, then changed by <paramref name="sql"/> the way damage or a mix-up would change it.</summary>
    private static Action<SqliteConnection, string> CopyThen(string sql, Action<string>? damage = null) => (source, path) =>
    {
        using (var command = source.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            copy.Open();
            using var change = copy.CreateCommand();
            change.CommandText = sql;
            change.ExecuteNonQuery();
        }

        damage?.Invoke(path);
    };

    /// <summary>Claims more cells than the table's page can hold, as a page torn by a failing disk reads.</summary>
    private static Action<string> DamageTable(string table) => path =>
    {
        long root;
        long pageSize;
        using (var inspect = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            inspect.Open();
            using var command = inspect.CreateCommand();
            command.CommandText = "SELECT rootpage, (SELECT page_size FROM pragma_page_size) FROM sqlite_schema WHERE name = $name";
            command.Parameters.AddWithValue("$name", table);
            using var reader = command.ExecuteReader();
            reader.Read();
            root = reader.GetInt64(0);
            pageSize = reader.GetInt64(1);
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Seek(((root - 1) * pageSize) + 3, SeekOrigin.Begin);
        file.Write([0xFF, 0xFF]);
    };

    private PreUpdateBackupException Refused(Action<SqliteConnection, string> copy, string fromRevision = "0036_drop_pruner_tables")
    {
        var database = OldDatabase();
        using var connection = database.Open();
        try
        {
            return Assert.Throws<PreUpdateBackupException>(() => Backup(copy).Save(connection, fromRevision));
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public void A_copy_that_does_not_pass_its_integrity_check_is_refused_and_removed()
    {
        var error = Refused(CopyThen("CREATE TABLE damaged (a INTEGER); INSERT INTO damaged VALUES (1), (2), (3);", DamageTable("damaged")));

        Assert.Contains("did not pass its integrity check", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Folder));
    }

    [Fact]
    public void A_copy_that_records_another_revision_is_refused_and_removed()
    {
        var error = Refused(CopyThen("UPDATE alembic_version SET version_num = '0001_other'"));

        Assert.Contains("records revision '0001_other', not '0036_drop_pruner_tables'", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Folder));
    }

    [Fact]
    public void A_revision_with_no_name_after_its_number_is_named_by_the_number()
    {
        var database = OldDatabase();
        using var connection = database.Open();

        var saved = Backup(CopyThen("UPDATE alembic_version SET version_num = '0036'")).Save(connection, "0036");
        database.ClearPool();

        Assert.Equal(Path.Join(Folder, "weir-0036-to-1.2.3-20261010T090000Z.db"), saved.DatabasePath);
    }

    [Fact]
    public void A_volume_that_will_not_say_how_much_room_it_has_is_no_reason_to_refuse()
    {
        Assert.Null(Backup().FreeSpaceOf("not\0a path"));

        var database = OldDatabase();
        using var connection = database.Open();
        var saved = Backup(freeSpace: _ => null).Save(connection, "0036_drop_pruner_tables");
        database.ClearPool();

        Assert.True(File.Exists(saved.DatabasePath));
    }

    [Theory]
    [InlineData("""{"FromVersion":"1.2.2"}""", "1.2.2", Version)]
    [InlineData("""{"FromVersion":7,"ToVersion":5}""", null, Version)]
    [InlineData("[]", null, Version)]
    [InlineData("""{"ToVersion":"1.2.4","FromVersion":"1.2.2"}""", "1.2.2", "1.2.4")]
    public void A_copy_is_described_by_its_details_file_and_by_its_name_where_the_details_say_nothing(string details, string? from, string to)
    {
        Directory.CreateDirectory(Folder);
        var stem = $"weir-0036-to-{Version}-{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        File.WriteAllText(Path.Join(Folder, $"{stem}.db"), "copy");
        File.WriteAllText(Path.Join(Folder, $"{stem}.backup.json"), details);

        var latest = PreUpdateBackup.Latest(BackupDir);

        Assert.Equal((from, to), (latest?.FromVersion, latest?.ToVersion));
    }

    [Fact]
    public void A_backup_cannot_be_made_without_somewhere_to_say_what_it_did() =>
        Assert.Throws<ArgumentNullException>("logger", () => new PreUpdateBackup(BackupDir, _home.Path, Version, _time, null!));
}
