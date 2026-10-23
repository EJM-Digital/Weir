using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The tray learns what to show from <c>tray-status.json</c>: the pause, the managers and folders that do not answer, and whether
/// the server is running. Nothing about files or downloads is in it.
/// </summary>
public sealed class TrayStatusWriterTests : IAsyncLifetime, IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly DataChangePublisher _changes = new();
    private readonly SuitePauseService _pause;
    private readonly CapturingLogger<TrayStatusWriter> _log = new();
    private readonly FolderReachability _folders;
    private readonly TimerWatchingClock _clock;
    private readonly TrayStatusWriter _writer;
    private readonly TempDirectory _shares = new();

    public TrayStatusWriterTests()
    {
        var settings = new SuiteSettingsStore(new AuthStore(), _changes);
        _pause = new SuitePauseService(settings, new ActivityStore(), _changes);
        _folders = new FolderReachability(
            _store.Database, new LibraryStore(), _store.Options, new FilesystemFolderProbe(), _changes, _store.Clock, NullLogger<FolderReachability>.Instance);
        _clock = new TimerWatchingClock(_store.Clock);
        _writer = new TrayStatusWriter(_store.Options, _store.Database, settings, _folders, new MediaManagerConnectionStore(), _changes, _clock, _log);
    }

    private string StatusPath => Path.Join(_store.Options.WeirHome, TrayStatus.FileName);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _writer.StopAsync(CancellationToken.None);
        await _folders.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _folders.Dispose();
        _shares.Dispose();
        _store.Dispose();
    }

    private async Task StartAsync()
    {
        await _folders.StartAsync(CancellationToken.None);
        await _folders.FirstLook;
        await _writer.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => _writer.Readings >= 1);
    }

    private JsonElement? Status()
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(StatusPath)).RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Moves the clock past the writer's pauses until the status says what the condition asks.</summary>
    private Task UntilAsync(Func<JsonElement, bool> condition) =>
        Eventually.ThatAdvancingAsync(_store.Clock, TrayStatusWriter.Settle, () => Status() is { } status && condition(status));

    /// <summary>Publishes a change to <paramref name="topic"/> and moves the clock on until the writer has read the status again.</summary>
    private async Task ReadAgainAfterAsync(string topic)
    {
        var readings = _writer.Readings;
        _changes.Publish(topic);
        await Eventually.ThatAdvancingAsync(_store.Clock, TrayStatusWriter.Settle, () => _writer.Readings > readings);
    }

    private static string[] Names(JsonElement status, string kind) =>
        [.. status.GetProperty("unreachable").GetProperty(kind).EnumerateArray().Select(name => name.GetString()!)];

    private Task<PauseState> PauseAsync(bool paused, long? minutes = null) =>
        _store.WithUnitOfWork(uow => _pause.ChangeAsync(uow, paused, minutes, keepEnd: false, true, Timestamp.UtcNow(_store.Clock), "alice"));

    private async Task SeedFilesAsync(params (string Status, string Reason, bool Held)[] files)
    {
        var library = await _store.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        for (var i = 0; i < files.Length; i++)
        {
            var (status, reason, held) = files[i];
            await _store.Execute(
                "INSERT INTO files (library_id, relative_path, status, status_reason, hold_until, last_seen_at) " +
                $"VALUES ({library}, 'Film {i}/film.mkv', '{status}', '{reason}', {(held ? "'2026-01-15 12:00:00'" : "NULL")}, CURRENT_TIMESTAMP)");
        }
    }

    /// <summary>A switched-on workflow whose three folders are made on disk, and their paths.</summary>
    private async Task<(string Watched, string Work, string Output)> AddWorkflowAsync(string name)
    {
        var watched = Directory.CreateDirectory(_shares.Join(name, "watched")).FullName;
        var work = Directory.CreateDirectory(_shares.Join(name, "work")).FullName;
        var output = Directory.CreateDirectory(_shares.Join(name, "output")).FullName;
        await _store.Execute(
            "INSERT INTO libraries (name, media_type, watched_folder, work_folder, output_folder) " +
            $"VALUES ('{name}', 'movie', '{watched}', '{work}', '{output}')");
        return (watched, work, output);
    }

    /// <summary>Moves the clock on until the folders have been looked at again and the status says what the condition asks.</summary>
    private Task UntilLookedAgainAsync(Func<JsonElement, bool> condition) =>
        Eventually.ThatAsync(() =>
        {
            _store.Clock.Advance(FolderReachability.Every);
            return Status() is { } status && condition(status);
        });

    [Fact]
    public async Task The_status_is_written_when_the_server_starts_saying_all_is_well()
    {
        await StartAsync();
        await UntilAsync(_ => true);

        Assert.Equal(
            "{\"paused\":false,\"paused_until\":null,\"unreachable\":{\"managers\":[],\"folders\":[]},\"server_ok\":true}",
            File.ReadAllText(StatusPath));
    }

    [Fact]
    public async Task A_server_with_no_settings_saved_yet_says_it_is_not_paused()
    {
        await _store.Execute("DELETE FROM suite_settings");

        await StartAsync();
        await UntilAsync(_ => true);

        Assert.False(Status()!.Value.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public void A_writer_that_was_never_started_can_be_let_go()
    {
        var exception = Record.Exception(_writer.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public async Task Pausing_and_resuming_are_written()
    {
        await StartAsync();
        await UntilAsync(_ => true);

        var pausedAt = _store.Clock.GetUtcNow();
        await PauseAsync(paused: true, minutes: 30);
        await UntilAsync(status => status.GetProperty("paused").GetBoolean());
        Assert.Equal(
            pausedAt.AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Status()!.Value.GetProperty("paused_until").GetString());

        await PauseAsync(paused: false);
        await UntilAsync(status => !status.GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Status()!.Value.GetProperty("paused_until").ValueKind);
    }

    [Fact]
    public async Task Files_that_wait_on_a_person_are_not_in_the_status_and_do_not_rewrite_it()
    {
        await StartAsync();
        await UntilAsync(_ => true);
        var written = File.GetLastWriteTimeUtc(StatusPath);

        await SeedFilesAsync(
            ("processing_failed", "Weir could not read the audio track.", false),
            ("rejected", "Nothing was left to keep.", false),
            ("on_hold", "Waiting for a decision.", false),
            ("skipped", "Skipped because this file is 3.0 MB, under the 10 MB minimum.", false));
        _changes.Publish(DataTopics.Jobs);
        _changes.Publish(DataTopics.LibraryScan);
        await ReadAgainAfterAsync(DataTopics.Connections);

        Assert.Equal(written, File.GetLastWriteTimeUtc(StatusPath));
        Assert.DoesNotContain("files", File.ReadAllText(StatusPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_managers_that_do_not_answer_are_named()
    {
        await StartAsync();
        await UntilAsync(_ => true);

        await _store.Execute(
            "INSERT INTO media_manager_connections (kind, name, enabled, base_url, last_connection_test_ok) VALUES " +
            "('deluno', 'Deluno on RIG', 1, 'http://192.0.2.10:5000', 0), " +
            "('radarr', 'Radarr on nas', 1, 'http://192.0.2.20:7878', 1), " +
            "('sonarr', 'Sonarr on nas', 1, 'http://192.0.2.20:8989', NULL), " +
            "('native', 'Native on box', 0, 'http://192.0.2.30:5000', 0)");
        _changes.Publish(DataTopics.Connections);

        await UntilAsync(status => Names(status, "managers").Length == 1);
        Assert.Equal(["Deluno on RIG"], Names(Status()!.Value, "managers"));
    }

    [Fact]
    public async Task Folders_that_cannot_be_reached_are_named_when_they_go_and_dropped_when_they_come_back()
    {
        var (watched, _, output) = await AddWorkflowAsync("Films");
        await StartAsync();
        await UntilAsync(_ => true);
        Assert.Empty(Names(Status()!.Value, "folders"));

        Directory.Delete(watched);
        await UntilLookedAgainAsync(status => Names(status, "folders").Length == 1);
        Assert.Equal(["The watched folder for Films"], Names(Status()!.Value, "folders"));

        Directory.Delete(output);
        await UntilLookedAgainAsync(status => Names(status, "folders").Length == 2);
        Assert.Equal(["The watched folder for Films", "The output folder for Films"], Names(Status()!.Value, "folders"));

        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await UntilLookedAgainAsync(status => Names(status, "folders").Length == 0);
    }

    [Fact]
    public async Task A_workflow_added_with_a_folder_that_is_not_there_is_looked_at_when_it_is_added_and_confirmed_soon_after()
    {
        await StartAsync();
        await UntilAsync(_ => true);

        await _store.Execute(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder) " +
            $"VALUES ('Series', 'tv', '{_shares.Join("gone", "watched")}', '{_shares.Path}')");
        _changes.Publish(DataTopics.Libraries);

        await Eventually.ThatAsync(() =>
        {
            _store.Clock.Advance(FolderReachability.ConfirmAfter);
            return Status() is { } status && Names(status, "folders").Length == 1;
        });
        Assert.Equal(["The watched folder for Series"], Names(Status()!.Value, "folders"));
    }

    [Fact]
    public async Task The_first_status_waits_for_the_folders_to_have_been_looked_at_but_not_for_ever()
    {
        await _writer.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => _clock.TimersSet >= 1);

        _store.Clock.Advance(TrayStatusWriter.FirstLookWait - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _writer.Readings);
        Assert.False(File.Exists(StatusPath));

        _store.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.ThatAsync(() => File.Exists(StatusPath));
    }

    [Fact]
    public async Task A_change_that_leaves_the_status_as_it_was_does_not_rewrite_the_file()
    {
        await StartAsync();
        await UntilAsync(_ => true);
        var written = File.GetLastWriteTimeUtc(StatusPath);

        await ReadAgainAfterAsync(DataTopics.Connections);
        await ReadAgainAfterAsync(DataTopics.FolderChecks);

        Assert.Equal(written, File.GetLastWriteTimeUtc(StatusPath));
        Assert.Empty(Directory.GetFiles(_store.Options.WeirHome, ".tray-status.json.*.tmp"));
    }

    [Fact]
    public async Task Stopping_the_server_says_it_is_no_longer_running_and_keeps_the_rest()
    {
        await StartAsync();
        await UntilAsync(_ => true);
        await PauseAsync(paused: true);
        await UntilAsync(status => status.GetProperty("paused").GetBoolean());

        await _writer.StopAsync(CancellationToken.None);

        var status = Status()!.Value;
        Assert.False(status.GetProperty("server_ok").GetBoolean());
        Assert.True(status.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task A_reading_that_fails_is_tried_again_so_an_old_file_never_stays_on_show()
    {
        await PauseAsync(paused: false);
        await File.WriteAllTextAsync(StatusPath, "{\"paused\":true,\"paused_until\":null,\"unreachable\":{\"managers\":[\"Deluno on RIG\"],\"folders\":[]},\"server_ok\":true}");
        await _store.Execute("UPDATE suite_settings SET processing_paused_until = 'not a time' WHERE id = 1");

        await StartAsync();
        await Eventually.ThatAsync(() => _log.Logged(LogLevel.Warning, "could not read what the tray shows"));
        Assert.True(Status()!.Value.GetProperty("paused").GetBoolean());

        await _store.Execute("UPDATE suite_settings SET processing_paused_until = NULL WHERE id = 1");
        await UntilAsync(status => !status.GetProperty("paused").GetBoolean());
        Assert.Empty(Names(Status()!.Value, "managers"));
    }

    [WindowsFact("A file that is open cannot be replaced only where file sharing is enforced.")]
    public async Task A_file_the_tray_is_reading_is_replaced_once_it_lets_go()
    {
        await StartAsync();
        await UntilAsync(_ => true);

        using (new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await PauseAsync(paused: true);
            await Eventually.ThatAdvancingAsync(_store.Clock, TrayStatusWriter.Settle, () => _log.Logged(LogLevel.Warning, "could not tell the tray how it is"));
            Assert.False(Status()!.Value.GetProperty("paused").GetBoolean());
        }

        await UntilAsync(status => status.GetProperty("paused").GetBoolean());
    }
}
