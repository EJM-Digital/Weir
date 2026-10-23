using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The copy taken before an update when something else touches the backup folder at the same time.</summary>
public sealed class PreUpdateBackupRaceTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly ListLogger<PreUpdateBackup> _logger = new();

    public void Dispose() => _home.Dispose();

    private string BackupDir => _home.Join("backups");

    private string Folder => PreUpdateBackup.FolderIn(BackupDir);

    private static string Stamped(DateTimeOffset at) =>
        $"weir-0036-to-1.2.3-{at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    private SqliteDatabase OldDatabase()
    {
        var path = _home.Join("data", "weir.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = new SqliteDatabase(path);
        new SchemaMigrator(database).EnsureAtBaseline();
        return database;
    }

    private PreUpdateBackup Backup() => new(BackupDir, _home.Path, "1.2.3", _time, _logger);

    private static void Upgrade(SqliteDatabase database, PreUpdateBackup backup) =>
        new SchemaMigrator(database).EnsureAtHead((connection, revision) => backup.Save(connection, revision));

    [Fact]
    public void A_pre_update_folder_that_cannot_be_read_means_there_is_no_copy_to_offer()
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(Folder, "in the way");

        Assert.Null(PreUpdateBackup.Latest(BackupDir));
    }

    [WindowsFact("Another program holding a file open blocks its removal on Windows only.")]
    public void An_old_copy_another_program_holds_stays_and_the_new_copy_is_still_saved()
    {
        Directory.CreateDirectory(Folder);
        var oldest = Path.Join(Folder, Stamped(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)) + ".db");
        foreach (var day in Enumerable.Range(1, 5))
        {
            File.WriteAllText(Path.Join(Folder, Stamped(new DateTimeOffset(2026, 9, day, 8, 0, 0, TimeSpan.Zero)) + ".db"), "old copy");
        }

        var database = OldDatabase();
        using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Upgrade(database, Backup());
        }

        database.ClearPool();
        Assert.True(File.Exists(oldest));
        Assert.NotNull(PreUpdateBackup.Latest(BackupDir));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), PreUpdateBackup.Latest(BackupDir)!.TakenAt);
    }

    [Fact]
    public void A_settings_copy_that_appears_while_the_database_is_copied_stops_the_backup_and_is_not_overwritten_or_removed()
    {
        File.WriteAllText(_home.Join("lan-access"), "on");
        var existing = Path.Join(Folder, Stamped(_time.GetUtcNow()) + ".lan-access");
        var database = OldDatabase();
        var backup = Backup();
        backup.CopyDatabase = (connection, path) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
            File.WriteAllText(existing, "someone else's copy");
        };

        var error = Assert.Throws<PreUpdateBackupException>(() => Upgrade(database, backup));
        database.ClearPool();

        Assert.Equal("Something unexpected went wrong. The details are in Weir's log files.", error.Reason);
        Assert.Contains(_logger.Exceptions, exception => exception.Message.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal("someone else's copy", File.ReadAllText(existing));
        Assert.Empty(Directory.EnumerateFiles(Folder, "*.db"));
    }
}
