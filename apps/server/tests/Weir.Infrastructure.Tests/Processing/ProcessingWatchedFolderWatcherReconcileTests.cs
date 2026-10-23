using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>The watch set following the workflows: one switched off, pointed at another folder or removed, and the scans an error leaves behind. The service's clock is fake, so each pass of its loop is one step of the test.</summary>
[Collection(WriteLockTimingGroup.Name)]
public sealed class ProcessingWatchedFolderWatcherReconcileTests : IDisposable
{
    private static readonly LibraryStore Libraries = new();

    private readonly StoreFixture _store = new(("WEIR_CREDENTIALS_SECRET", "watcher-reconcile-secret"), ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "60"));
    private readonly TimerWatchingClock _clock = new(new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)));
    private readonly ListLogger<ProcessingWatchedFolderWatcherService> _log = new();
    private readonly WatcherStateStore _state = new();
    private readonly ScanSettingsChanges _changes = new();
    private readonly string _watched;
    private readonly string _output;
    private readonly RecordingService _service;

    public ProcessingWatchedFolderWatcherReconcileTests()
    {
        _watched = _store.Home.Join("watch");
        _output = _store.Home.Join("out");
        Directory.CreateDirectory(_watched);
        Directory.CreateDirectory(_output);
        _service = new RecordingService(_store, _state, _clock, _changes, _log);
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
    }

    private async Task<long> ConfigureAsync(Func<ProcessingLibraryInput, ProcessingLibraryInput> change)
    {
        await using var uow = await UnitOfWork.OpenAsync(_store.Database);
        var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
        var input = new ProcessingLibraryInput { Name = seeded.Name, MediaType = ProcessingMediaScopes.Movie, WatchedFolder = _watched, OutputFolder = _output };
        var updated = await Libraries.UpdateAsync(uow, seeded, change(input));
        await uow.CommitAsync();
        return updated.Id;
    }

    /// <summary>Starts the service and waits out its first pass, which reconciles the watch set and parks the loop on the clock.</summary>
    private async Task<long> StartWatchingAsync()
    {
        var libraryId = await ConfigureAsync(input => input);
        await _service.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => _clock.TimersSet >= 1);
        return libraryId;
    }

    /// <summary>Moves the clock one tick on and waits for the loop to finish that pass and park again.</summary>
    private async Task TickAsync()
    {
        var timers = _clock.TimersSet;
        _clock.Advance(ProcessingWatchedFolderWatcherService.TickInterval);
        await Eventually.ThatAsync(() => _clock.TimersSet > timers);
    }

    private int ScanJobs()
    {
        using var connection = _store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE job_kind = @kind";
        command.Parameters.AddWithValue("@kind", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_workflow_switched_to_scan_only_stops_being_watched_and_says_why()
    {
        var libraryId = await StartWatchingAsync();
        var watcher = _service.Latest(_watched);
        Assert.True(watcher.EnableRaisingEvents);

        await ConfigureAsync(input => input with { FileSystemEventsEnabled = false });
        _changes.Record();
        await TickAsync();

        var report = _state.Reports().Single(r => r.LibraryId == libraryId);
        Assert.Equal(WatcherStatus.Disabled, report.Status);
        Assert.Contains("so Weir finds new files on the scan interval", report.Detail, StringComparison.Ordinal);
        Assert.False(watcher.EnableRaisingEvents);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_workflow_pointed_at_another_folder_is_watched_there_and_no_longer_in_the_old_one()
    {
        var moved = _store.Home.Join("moved");
        Directory.CreateDirectory(moved);
        var libraryId = await StartWatchingAsync();
        var old = _service.Latest(_watched);

        await ConfigureAsync(input => input with { WatchedFolder = moved });
        _changes.Record();
        await TickAsync();

        var report = _state.Reports().Single(r => r.LibraryId == libraryId);
        Assert.Equal(moved, report.WatchedFolder);
        Assert.Equal(WatcherStatus.Watching, report.Status);
        Assert.False(old.EnableRaisingEvents);
        Assert.True(_service.Latest(moved).EnableRaisingEvents);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_change_that_leaves_the_watched_folder_alone_keeps_the_same_watcher()
    {
        await StartWatchingAsync();
        var first = _service.Latest(_watched);

        await ConfigureAsync(input => input with { MaxAttempts = 5 });
        _changes.Record();
        await TickAsync();

        Assert.Single(_service.Created(_watched));
        Assert.True(first.EnableRaisingEvents);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_error_from_a_watcher_whose_workflow_was_switched_off_restarts_nothing_and_queues_no_scan()
    {
        var libraryId = await StartWatchingAsync();
        var watcher = _service.Latest(_watched);
        await ConfigureAsync(input => input with { Enabled = false });
        _changes.Record();
        await TickAsync();
        Assert.DoesNotContain(_state.Reports(), r => r.LibraryId == libraryId);

        watcher.RaiseError(new IOException("simulated overflow"));
        await TickAsync();

        Assert.Single(_service.Created(_watched));
        Assert.Equal(0, ScanJobs());
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_error_heard_just_before_the_workflow_is_switched_off_restarts_nothing_and_queues_no_scan()
    {
        await StartWatchingAsync();
        _service.Latest(_watched).RaiseError(new IOException("simulated overflow"));
        await ConfigureAsync(input => input with { Enabled = false });
        _changes.Record();

        await TickAsync();

        Assert.Single(_service.Created(_watched));
        Assert.Equal(0, ScanJobs());
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Workflow_settings_that_cannot_be_read_are_logged_and_the_watch_set_is_kept()
    {
        await StartWatchingAsync();
        var watcher = _service.Latest(_watched);
        using (var connection = _store.Database.Open())
        {
            using var rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE libraries RENAME TO libraries_unavailable";
            rename.ExecuteNonQuery();
        }

        _changes.Record();
        await TickAsync();

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("could not read workflow settings", StringComparison.Ordinal));
        Assert.True(watcher.EnableRaisingEvents);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_scan_that_cannot_be_queued_is_logged_and_the_watcher_is_still_restarted()
    {
        await StartWatchingAsync();
        using (var connection = _store.Database.Open())
        {
            using var rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE jobs RENAME TO jobs_unavailable";
            rename.ExecuteNonQuery();
        }

        _service.Latest(_watched).RaiseError(new IOException("simulated overflow"));
        await TickAsync();

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("could not queue a scan", StringComparison.Ordinal));
        Assert.Equal(2, _service.Created(_watched).Count);
        await _service.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingService(
        StoreFixture store, WatcherStateStore state, TimeProvider clock, ScanSettingsChanges changes, ILogger<ProcessingWatchedFolderWatcherService> log)
        : ProcessingWatchedFolderWatcherService(
            store.Database, store.Options, new ProcessingJobStore(store.Database, clock), state, Libraries, clock, log, changes)
    {
        private readonly ConcurrentDictionary<string, List<Watcher>> _created = new(StringComparer.Ordinal);

        public IReadOnlyList<Watcher> Created(string folder) =>
            _created.TryGetValue(Path.GetFullPath(folder), out var list) ? [.. list] : [];

        public Watcher Latest(string folder) => Created(folder)[^1];

        internal override FileSystemWatcher CreateFileSystemWatcher(string folder) => new Watcher(folder);

        internal override void OnWatcherReady(long libraryId, FileSystemWatcher watcher) =>
            _created.AddOrUpdate(
                Path.GetFullPath(watcher.Path),
                _ => [(Watcher)watcher],
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.Add((Watcher)watcher);
                        return existing;
                    }
                });

        public sealed class Watcher(string path) : FileSystemWatcher(path)
        {
            public void RaiseError(Exception exception) => OnError(new ErrorEventArgs(exception));
        }
    }
}
