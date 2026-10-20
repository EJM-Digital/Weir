using Weir.Core.Notifications;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Activity;

/// <summary>
/// What Setup, Connections and Settings tell the live stream: each store that writes them publishes its topic once the write
/// commits, so every open screen reads the data again without a reload.
/// </summary>
public sealed class SetupDataChangeTests : IDisposable
{
    private const string EndOfChanges = "end-of-changes";

    private readonly StoreFixture _store = new();
    private readonly DataChangePublisher _changes = new();
    private readonly MediaManagerConnectionStore _managers;
    private readonly DownloadClientConnectionStore _clients;
    private readonly LibraryStore _libraries;
    private readonly SuiteSettingsStore _suite;
    private readonly OperatorSettingsStore _operator;
    private readonly NotificationChannelStore _channels;
    private readonly FileSkipMarkerStore _kept;
    private readonly ConfigurationBundleStore _bundle;
    private readonly ConfigurationBackups _backups;

    public SetupDataChangeTests()
    {
        _managers = new MediaManagerConnectionStore(changes: _changes);
        _clients = new DownloadClientConnectionStore(changes: _changes);
        _libraries = new LibraryStore(_changes);
        _suite = new SuiteSettingsStore(_store.Users, _changes);
        _operator = new OperatorSettingsStore(_changes);
        _channels = new NotificationChannelStore(_changes);
        _kept = new FileSkipMarkerStore(_changes);
        _bundle = new ConfigurationBundleStore(_suite, BackupParts.Connections(_channels, _managers, _clients), _changes);
        _backups = new ConfigurationBackups(_store.Options, _store.Clock, new IanaTimeZoneResolver(), _suite, _bundle, BackupParts.Passwords(_changes), _changes);
    }

    public void Dispose() => _store.Dispose();

    private Timestamp Now => Timestamp.UtcNow(_store.Clock);

    /// <summary>Every topic published while <paramref name="work"/> ran and committed, in order.</summary>
    private async Task<string[]> HeardAsync(Func<UnitOfWork, Task> work, bool commit = true)
    {
        using var stream = _changes.Subscribe();
        await _store.WithUnitOfWork(async uow =>
        {
            await work(uow);
            return true;
        }, commit);
        _changes.Publish(EndOfChanges);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heard = new List<string>();
        await foreach (var topic in stream.ReadAllAsync(timeout.Token))
        {
            if (topic == EndOfChanges)
            {
                break;
            }

            heard.Add(topic);
        }

        return [.. heard];
    }

    [Fact]
    public async Task A_change_is_published_once_after_the_unit_of_work_commits_however_often_it_is_announced()
    {
        var heard = await HeardAsync(uow =>
        {
            _changes.PublishOnCommit(uow, DataTopics.Settings);
            _changes.PublishOnCommit(uow, DataTopics.Settings);
            _changes.PublishOnCommit(uow, DataTopics.Backups);
            return Task.CompletedTask;
        });

        Assert.Equal([DataTopics.Settings, DataTopics.Backups], heard);
    }

    [Fact]
    public async Task A_change_that_rolls_back_is_not_published()
    {
        var heard = await HeardAsync(
            uow =>
            {
                _changes.PublishOnCommit(uow, DataTopics.Settings);
                return Task.CompletedTask;
            },
            commit: false);

        Assert.Empty(heard);
    }

    [Fact]
    public async Task Every_write_to_a_media_manager_connection_publishes_the_connections()
    {
        var id = 0L;
        Assert.Equal([DataTopics.Connections], await HeardAsync(async uow => id = await _managers.InsertAsync(uow, "sonarr", true, "http://sonarr.local", null)));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _managers.UpdateColumnsAsync(uow, id, [("enabled", 0)])));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _managers.RecordTestResultAsync(uow, id, true, Now, "Connected.")));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _managers.DeleteAsync(uow, id)));
    }

    [Fact]
    public async Task A_test_result_for_a_connection_that_is_gone_publishes_nothing()
    {
        Assert.Empty(await HeardAsync(uow => _managers.RecordTestResultAsync(uow, 404, false, Now, "Not there.")));
    }

    [Fact]
    public async Task Every_write_to_a_download_client_connection_publishes_the_connections()
    {
        var id = 0L;
        Assert.Equal([DataTopics.Connections], await HeardAsync(async uow => id = await _clients.InsertAsync(uow, "qbittorrent", true, "http://client.local", null, null, null)));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _clients.UpdateColumnsAsync(uow, id, [("enabled", 0)])));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _clients.RecordTestResultAsync(uow, id, false, Now, "Not answering.")));
        Assert.Equal([DataTopics.Connections], await HeardAsync(uow => _clients.DeleteAsync(uow, id)));
        Assert.Empty(await HeardAsync(uow => _clients.RecordTestResultAsync(uow, id, true, Now, "Connected.")));
    }

    [Fact]
    public async Task Every_write_to_a_workflow_publishes_the_libraries()
    {
        var input = new ProcessingLibraryInput { Name = "4K", MediaType = "movie" };
        ProcessingLibraryRecord? created = null;
        Assert.Equal([DataTopics.Libraries], await HeardAsync(async uow => created = await _libraries.CreateAsync(uow, input)));
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.UpdateAsync(uow, created!, input with { Name = "4K movies" })));
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.UnlinkAsync(uow, created!)));
        var everyId = (await _store.WithUnitOfWork(uow => _libraries.ListAsync(uow))).Select(row => row.Id).Reverse().ToArray();
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.ReorderAsync(uow, everyId)));
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.DeleteAsync(uow, created!)));
    }

    [Fact]
    public async Task Linking_a_workflow_to_a_manager_publishes_the_libraries_and_linking_it_again_does_not()
    {
        var connection = await _store.WithUnitOfWork(uow => _managers.InsertAsync(uow, "deluno", true, "http://deluno.local", null));
        var library = (await _store.WithUnitOfWork(uow => _libraries.ListAsync(uow))).First();

        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.SetManagerLinksAsync(uow, library.Id, [connection])));
        Assert.Empty(await HeardAsync(uow => _libraries.SetManagerLinksAsync(uow, library.Id, [connection])));
    }

    [Fact]
    public async Task What_the_workflow_sync_writes_publishes_the_libraries()
    {
        var connection = await _store.WithUnitOfWork(uow => _managers.InsertAsync(uow, "deluno", true, "http://deluno.local", null));
        var library = (await _store.WithUnitOfWork(uow => _libraries.ListAsync(uow))).First();

        Assert.Equal(
            [DataTopics.Libraries],
            await HeardAsync(uow => _libraries.AdoptForManagerAsync(uow, library, "Movies from Deluno", connection, "movies", @"c:\media\in", @"c:\media\out")));
        Assert.Equal(
            [DataTopics.Libraries],
            await HeardAsync(uow => _libraries.SetFoldersFromManagerAsync(uow, library, @"c:\media\in2", null)));
    }

    [Fact]
    public async Task Every_write_to_a_profile_publishes_the_libraries()
    {
        ProcessingRuleSetRecord? profile = null;
        var input = new LibraryRules.RuleSetInput { Name = "Spare" };
        Assert.Equal([DataTopics.Libraries], await HeardAsync(async uow => profile = await _libraries.CreateRuleSetAsync(uow, input)));
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.UpdateRuleSetAsync(uow, profile!, input with { Name = "Spare two" })));
        Assert.Equal([DataTopics.Libraries], await HeardAsync(uow => _libraries.DeleteRuleSetAsync(uow, profile!)));
    }

    [Fact]
    public async Task Saving_settings_publishes_the_settings_and_saving_them_unchanged_does_not()
    {
        var suite = await _store.WithUnitOfWork(uow => _suite.EnsureAsync(uow));
        var operatorSettings = await _store.WithUnitOfWork(uow => _operator.EnsureAsync(uow));

        Assert.Equal([DataTopics.Settings], await HeardAsync(uow => _suite.UpdateAsync(uow, suite, suite with { LogRetentionDays = suite.LogRetentionDays + 1 })));
        Assert.Equal([DataTopics.Settings], await HeardAsync(uow => _operator.UpdateAsync(uow, operatorSettings, operatorSettings with { KeepFailedWorkFiles = !operatorSettings.KeepFailedWorkFiles })));
        Assert.Empty(await HeardAsync(uow => _suite.UpdateAsync(uow, suite, suite)));
        Assert.Empty(await HeardAsync(uow => _operator.UpdateAsync(uow, operatorSettings, operatorSettings)));
    }

    [Fact]
    public async Task Choosing_playback_devices_publishes_the_devices_and_not_the_settings()
    {
        var suite = await _store.WithUnitOfWork(uow => _suite.EnsureAsync(uow));

        Assert.Equal([DataTopics.DirectPlayDevices], await HeardAsync(uow => _suite.UpdateAsync(uow, suite, suite with { DirectPlayDevices = "iphone" })));
        Assert.Equal(
            [DataTopics.DirectPlayDevices, DataTopics.Settings],
            await HeardAsync(uow => _suite.UpdateAsync(uow, suite, suite with { DirectPlayDevices = "iphone", LogRetentionDays = suite.LogRetentionDays + 1 })));
    }

    [Fact]
    public async Task Pausing_changes_the_pause_and_not_the_settings()
    {
        var suite = await _store.WithUnitOfWork(uow => _suite.EnsureAsync(uow));

        Assert.Empty(await HeardAsync(uow => _suite.UpdateAsync(uow, suite, suite with { ProcessingPaused = true, ScanWhilePaused = true })));
    }

    [Fact]
    public async Task Every_write_to_an_alert_channel_publishes_the_settings()
    {
        NotificationChannelRecord? channel = null;
        Assert.Equal(
            [DataTopics.Settings],
            await HeardAsync(async uow => channel = await _channels.CreateAsync(uow, "Phone", "discord", "https://discord.test/hook", [], true)));
        Assert.Equal(
            [DataTopics.Settings],
            await HeardAsync(uow => _channels.UpdateAsync(uow, channel!, "Phone", "discord", "https://discord.test/hook", [], false)));
        Assert.Equal([DataTopics.Settings], await HeardAsync(uow => _channels.DeleteAsync(uow, channel!.Id)));
        Assert.Empty(await HeardAsync(uow => _channels.DeleteAsync(uow, channel!.Id)));
    }

    [Fact]
    public async Task A_configuration_backup_publishes_the_backups()
    {
        Assert.Equal([DataTopics.Backups], await HeardAsync(uow => _backups.CreateAsync(uow, BackupSource.Manual)));
    }

    [Fact]
    public async Task A_restore_publishes_the_workflows_and_settings_it_rewrote()
    {
        var bundle = await _store.WithUnitOfWork(_bundle.BuildAsync, commit: false);

        var heard = await HeardAsync(uow => _bundle.ApplyAsync(uow, bundle, new IanaTimeZoneResolver(), _store.Options.WeirHome));

        Assert.Equal([DataTopics.Libraries, DataTopics.Settings], [.. heard.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task Keeping_a_file_and_processing_it_again_publish_the_kept_files()
    {
        var library = (await _store.WithUnitOfWork(uow => _libraries.ListAsync(uow))).First();
        Assert.Equal([DataTopics.KeptFiles], await HeardAsync(uow => _kept.SetAsync(uow, library.Id, "Film.mkv", 10, 20)));
        var marker = (await _store.WithUnitOfWork(uow => _kept.ListAsync(uow))).Single();

        Assert.Equal([DataTopics.KeptFiles], await HeardAsync(uow => _kept.ClearByIdAsync(uow, marker.Id)));
        Assert.Empty(await HeardAsync(uow => _kept.ClearByIdAsync(uow, marker.Id)));
        Assert.Empty(await HeardAsync(uow => _kept.ClearAsync(uow, library.Id, "Film.mkv")));
    }
}
