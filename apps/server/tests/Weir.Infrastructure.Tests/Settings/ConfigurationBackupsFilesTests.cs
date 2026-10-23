using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>Finding a stored snapshot's file, and tidying the backup folder, against real files.</summary>
public sealed class ConfigurationBackupsFilesTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly ConfigurationBackups _backups;

    public ConfigurationBackupsFilesTests()
    {
        var suite = new SuiteSettingsStore(_store.Users);
        _backups = new ConfigurationBackups(
            _store.Options, _store.Clock, new IanaTimeZoneResolver(), suite,
            new ConfigurationBundleStore(suite, BackupParts.Connections()), BackupParts.Passwords());
    }

    public void Dispose() => _store.Dispose();

    private async Task<long> RecordAsync(string fileName, string createdAt)
    {
        await _store.Execute(
            $"INSERT INTO suite_configuration_backup (created_at, file_name, size_bytes) VALUES ('{createdAt}', '{fileName}', 2)");
        return await _store.Scalar("SELECT max(id) FROM suite_configuration_backup");
    }

    private Task<(string Path, ConfigurationBackupRecord Row)> FileOfAsync(long id) =>
        _store.WithUnitOfWork(uow => _backups.GetFileAsync(uow, id), commit: false);

    [Fact]
    public async Task A_snapshot_that_is_not_recorded_is_not_found()
    {
        var failure = await Assert.ThrowsAsync<WireValueException>(() => FileOfAsync(12345));

        Assert.Equal("Configuration snapshot not found.", failure.Message);
    }

    [Theory]
    [InlineData("other.json")]
    [InlineData("sub/suite-configuration-1.json")]
    [InlineData("../suite-configuration-1.json")]
    [InlineData("C:suite-configuration-1.json")]
    [InlineData("C:")]
    [InlineData(".")]
    public async Task A_recorded_name_that_is_not_a_plain_snapshot_name_is_refused(string name)
    {
        var id = await RecordAsync(name, "2026-01-01 00:00:00.000000");

        var failure = await Assert.ThrowsAsync<WireValueException>(() => FileOfAsync(id));

        Assert.Equal("Configuration snapshot file name is invalid.", failure.Message);
    }

    [Fact]
    public async Task A_recorded_snapshot_whose_file_is_gone_says_so()
    {
        var id = await RecordAsync("suite-configuration-gone.json", "2026-01-01 00:00:00.000000");

        var failure = await Assert.ThrowsAsync<WireValueException>(() => FileOfAsync(id));

        Assert.Equal("Configuration snapshot file is missing on disk.", failure.Message);
    }

    [Fact]
    public async Task A_recorded_snapshot_with_its_file_is_found_inside_the_backup_folder()
    {
        Directory.CreateDirectory(_backups.Directory);
        var file = Path.Join(_backups.Directory, "suite-configuration-here.json");
        await File.WriteAllTextAsync(file, "{}");
        var id = await RecordAsync("suite-configuration-here.json", "2026-01-01 00:00:00.000000");

        var (path, row) = await FileOfAsync(id);

        Assert.Equal(file, path);
        Assert.Equal(id, row.Id);
    }

    [Fact]
    public async Task Pruning_removes_snapshot_files_nothing_records_and_survives_one_it_cannot_delete()
    {
        Directory.CreateDirectory(_backups.Directory);
        var stray = Path.Join(_backups.Directory, "suite-configuration-stray.json");
        await File.WriteAllTextAsync(stray, "{}");
        var other = Path.Join(_backups.Directory, "notes.txt");
        await File.WriteAllTextAsync(other, "mine");
        // The oldest record's "file" is a folder, which cannot be deleted as a file.
        var stuck = Path.Join(_backups.Directory, "suite-configuration-0.json");
        Directory.CreateDirectory(stuck);
        for (var number = 0; number < ConfigurationBackups.MaxFiles; number++)
        {
            await RecordAsync($"suite-configuration-{number}.json", $"2026-01-0{number + 1} 00:00:00.000000");
        }

        await _store.WithUnitOfWork(uow => _backups.CreateAsync(uow, BackupSource.Manual));

        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(stuck));
        Assert.Equal(ConfigurationBackups.MaxFiles, await _store.Scalar("SELECT count(*) FROM suite_configuration_backup"));
    }
}
