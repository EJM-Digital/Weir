using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The copy taken before an update when the disk refuses, fills up or is in use part-way through.</summary>
public sealed class PreUpdateBackupFaultTests : IDisposable
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

    private PreUpdateBackup Backup(FaultingFileSystem? files = null) => new(BackupDir, _home.Path, "1.2.3", _time, _logger, files: files);

    private static PreUpdateBackupFile Upgrade(SqliteDatabase database, PreUpdateBackup backup)
    {
        PreUpdateBackupFile? saved = null;
        new SchemaMigrator(database).EnsureAtHead((connection, revision) => saved = backup.Save(connection, revision));
        return saved!;
    }

    private string OlderCopy(int day)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Join(Folder, Stamped(new DateTimeOffset(2026, 9, day, 8, 0, 0, TimeSpan.Zero)) + ".db");
        File.WriteAllText(path, "older copy");
        return path;
    }

    private string[] FolderFiles() => Directory.Exists(Folder) ? [.. Directory.EnumerateFiles(Folder).Select(Path.GetFileName).Order()!] : [];

    /// <summary>Runs the update against a disk that fails as given and returns what the person is told.</summary>
    private string Refusal(FaultingFileSystem files)
    {
        var database = OldDatabase();
        try
        {
            return Assert.Throws<PreUpdateBackupException>(() => Upgrade(database, Backup(files))).Reason;
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public void A_copy_the_disk_will_not_let_into_place_is_refused_in_plain_words_and_leaves_nothing_behind()
    {
        var older = OlderCopy(1);
        var files = new FaultingFileSystem().Fail(FileCall.MoveFile, FaultingFileSystem.Refused);

        var reason = Refusal(files);

        Assert.StartsWith("Weir isn't allowed to write to ", reason, StringComparison.Ordinal);
        Assert.EndsWith("Give the account Weir runs as full access to that folder, then try again.", reason, StringComparison.Ordinal);
        Assert.Equal([Path.GetFileName(older)], FolderFiles());
        Assert.Single(_logger.Exceptions);
    }

    [Fact]
    public void A_settings_copy_another_program_holds_stops_the_backup_and_removes_the_copy_already_made()
    {
        File.WriteAllText(_home.Join("lan-access"), "on");
        var older = OlderCopy(1);
        var files = new FaultingFileSystem().Fail(FileCall.CopyFile, FaultingFileSystem.SharingViolation);

        var reason = Refusal(files);

        Assert.Equal("Another program is using Weir's database or its backup folder. Close it, then try again.", reason);
        Assert.Equal([Path.GetFileName(older)], FolderFiles());
    }

    [Fact]
    public void A_full_disk_while_the_details_are_written_says_the_drive_is_full_and_removes_the_whole_copy()
    {
        File.WriteAllText(_home.Join("lan-access"), "on");
        const int diskFull = unchecked((int)0x80070070);
        var files = new FaultingFileSystem().Fail(FileCall.WriteAllBytes, path => new IOException($"There is not enough space on the disk. : '{path}'", diskFull));

        var reason = Refusal(files);

        Assert.Contains("is full. Free up some space there, then try again.", reason, StringComparison.Ordinal);
        Assert.Empty(FolderFiles());
    }

    [Fact]
    public void A_backup_folder_that_cannot_be_created_names_the_folder_and_changes_nothing()
    {
        var files = new FaultingFileSystem().Fail(FileCall.CreateDirectory, FaultingFileSystem.Refused);

        var reason = Refusal(files);

        Assert.Contains(Folder, reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Folder));
        Assert.Contains(_logger.Exceptions, exception => exception is UnauthorizedAccessException);
    }

    [Fact]
    public void A_database_that_vanishes_before_it_is_measured_is_an_unexpected_failure_that_says_so()
    {
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished);

        var reason = Refusal(files);

        Assert.Equal("Something unexpected went wrong. The details are in Weir's log files.", reason);
        Assert.Contains(_logger.Exceptions, exception => exception is FileNotFoundException);
    }

    [Fact]
    public void A_half_written_copy_that_cannot_be_removed_is_removed_by_the_next_backup_and_does_not_stop_it()
    {
        var database = OldDatabase();
        var failing = new FaultingFileSystem()
            .Fail(FileCall.MoveFile, FaultingFileSystem.Refused)
            .Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, onCall: 2, pathEndsWith: ".db.partial");
        Assert.Throws<PreUpdateBackupException>(() => Upgrade(database, Backup(failing)));
        var leftover = Directory.EnumerateFiles(Folder, "*.db.partial").Single();

        _time.Advance(TimeSpan.FromMinutes(5));
        var saved = Upgrade(database, Backup());
        database.ClearPool();

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(saved.DatabasePath));
        Assert.Equal(_time.GetUtcNow(), PreUpdateBackup.Latest(BackupDir)!.TakenAt);
    }

    [Fact]
    public void Older_copies_that_cannot_be_listed_are_left_alone_with_a_warning_and_the_new_copy_is_still_saved()
    {
        var older = OlderCopy(1);
        var database = OldDatabase();
        var files = new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused);

        var saved = Upgrade(database, Backup(files));
        database.ClearPool();

        Assert.True(File.Exists(older));
        Assert.True(File.Exists(saved.DatabasePath));
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("older copies", StringComparison.Ordinal));
    }

    [Fact]
    public void An_old_copy_that_cannot_be_removed_stays_and_the_new_copy_is_still_saved()
    {
        var copies = Enumerable.Range(1, 5).Select(OlderCopy).ToList();
        var database = OldDatabase();
        var oldest = Path.GetFileName(copies[0]);
        var files = new FaultingFileSystem()
            .Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, pathEndsWith: oldest)
            .Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, onCall: 2, pathEndsWith: oldest);

        var saved = Upgrade(database, Backup(files));
        database.ClearPool();

        Assert.True(File.Exists(copies[0]));
        Assert.True(File.Exists(saved.DatabasePath));
        Assert.Equal(_time.GetUtcNow(), PreUpdateBackup.Latest(BackupDir)!.TakenAt);
    }

    [Fact]
    public void A_copy_whose_details_cannot_be_read_is_still_offered_with_the_version_its_name_gives()
    {
        var database = OldDatabase();
        var written = new PreUpdateBackup(BackupDir, _home.Path, "1.2.3", _time, _logger, fromVersion: "1.2.0");
        Upgrade(database, written);
        database.ClearPool();
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused, pathEndsWith: ".backup.json");

        var offered = PreUpdateBackup.Latest(BackupDir, files)!;

        Assert.Equal("1.2.3", offered.ToVersion);
        Assert.Null(offered.FromVersion);
        Assert.Equal("1.2.0", PreUpdateBackup.Latest(BackupDir)!.FromVersion);
    }

    [Fact]
    public void A_backup_folder_the_system_refuses_to_list_means_there_is_no_copy_to_offer()
    {
        var database = OldDatabase();
        Upgrade(database, Backup());
        database.ClearPool();
        var files = new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused);

        Assert.Null(PreUpdateBackup.Latest(BackupDir, files));
        Assert.NotNull(PreUpdateBackup.Latest(BackupDir));
    }

    [PosixFact("Setting the permissions of a folder is a Unix call.")]
    public void A_folder_whose_permissions_cannot_be_set_is_not_used_and_the_person_is_told_who_to_give_access()
    {
        var files = new FaultingFileSystem().Fail(FileCall.SetUnixFileMode, FaultingFileSystem.Refused, pathEndsWith: PreUpdateBackup.FolderName);

        var reason = Refusal(files);

        Assert.StartsWith("Weir isn't allowed to write to ", reason, StringComparison.Ordinal);
        Assert.Empty(FolderFiles());
    }

    [PosixFact("Setting the permissions of a copy is a Unix call.")]
    public void A_copy_whose_permissions_cannot_be_set_is_removed_rather_than_left_readable()
    {
        var files = new FaultingFileSystem().Fail(FileCall.SetUnixFileMode, FaultingFileSystem.Refused, pathEndsWith: ".db");

        var reason = Refusal(files);

        Assert.StartsWith("Weir isn't allowed to write to ", reason, StringComparison.Ordinal);
        Assert.Empty(FolderFiles());
    }

    [PosixFact("The owner-only modes are a Unix feature; Windows uses an access list.")]
    [UnsupportedOSPlatform("windows")]
    public void The_folder_and_the_copies_are_owner_only()
    {
        File.WriteAllText(_home.Join("lan-access"), "on");
        var database = OldDatabase();

        var saved = Upgrade(database, Backup());
        database.ClearPool();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Folder));
        Assert.All(Directory.EnumerateFiles(Folder), path => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)));
        Assert.True(File.Exists(saved.DatabasePath));
    }
}
