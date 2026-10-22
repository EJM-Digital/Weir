using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// The watched-folder watcher, including the two behaviours issue #552 asks for: an overflow/error restarts
/// the watcher and forces an immediate scan, and readiness reports the watched libraries. One test per
/// admission path drives a real temp folder and a real <see cref="FileSystemWatcher"/>, tagged
/// <c>Category=Integration</c>, since that is the only way to prove the OS actually raises the events Weir
/// subscribes to; every other test drives the debounce and reconcile-tick logic through
/// <see cref="FakeWatcherService"/> and a shared <see cref="FakeTimeProvider"/>, so nothing here depends on
/// real chunk-write timing landing on the right side of a debounce window. In <see cref="WriteLockTimingGroup"/>: this
/// class's real timers and OS events must not share the process with a bulk-job test's dedicated hammering thread (#708).
/// </summary>
[Collection(WriteLockTimingGroup.Name)]
public sealed class ProcessingWatchedFolderWatcherServiceTests
{
    private const string ScanJobKind = ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch;

    /// <summary>The longest a wait for something the test has already arranged may take before the test calls it hung.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);
    private static readonly LibraryStore Libraries = new();

    private static async Task<long> CreateLibraryAsync(
        StoreFixture store, string watched, string output, bool fileSystemEventsEnabled = true)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
        var updated = await Libraries.UpdateAsync(uow, seeded, new ProcessingLibraryInput
        {
            Name = seeded.Name,
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = watched,
            OutputFolder = output,
            FileSystemEventsEnabled = fileSystemEventsEnabled,
        });
        await uow.CommitAsync();
        return updated.Id;
    }

    private static List<string> ScanJobPayloads(StoreFixture store)
    {
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM jobs WHERE job_kind = @kind";
        command.Parameters.AddWithValue("@kind", ScanJobKind);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return rows;
    }

    private static ProcessingWatchedFolderWatcherService Service(StoreFixture store, WatcherStateStore state, TimeProvider? time = null) =>
        new(store.Database, store.Options, new ProcessingJobStore(store.Database, time ?? TimeProvider.System), state, Libraries,
            time ?? TimeProvider.System, NullLogger<ProcessingWatchedFolderWatcherService>.Instance);

    // --- one admission implementation: a settled burst becomes one scan --------------------------------

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_file_appearing_becomes_a_candidate_without_waiting_for_the_scan_interval()
    {
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-1"),
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "1"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output);

        var state = new WatcherStateStore();
        var service = Service(store, state);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Wait for the watcher itself to report Watching rather than guessing how long the first
            // reconcile tick takes: on a loaded CI box that tick can take longer than any fixed sleep, and
            // writing the file before FileSystemWatcher.EnableRaisingEvents is actually true means Windows
            // never raises the event at all, so the test would wait out its full timeout for nothing.
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
            await File.WriteAllBytesAsync(Path.Combine(watched, "Gate Test 2001.mkv"), new byte[2048]);

            await Eventually.ThatAsync(() => ScanJobPayloads(store).Count > 0);
            var payloads = ScanJobPayloads(store);
            Assert.Single(payloads);
            var body = (WireObject)WireJsonParser.Parse(payloads[0]);
            Assert.Equal("filesystem_event", ((WireString)body.Get("scan_trigger")!).Value);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Several_files_deleted_from_a_watched_folder_queue_one_scan_so_files_that_left_are_found()
    {
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-3"),
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "1"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var files = Enumerable.Range(1, 6).Select(index => Path.Combine(watched, $"Gate Test {index}.mkv")).ToList();
        foreach (var file in files)
        {
            await File.WriteAllBytesAsync(file, new byte[2048]);
        }

        var libraryId = await CreateLibraryAsync(store, watched, output);

        // The debounce clock is fake, as in the growing-file test, so a slow machine cannot straddle a real second between deletes.
        var time = new TimerWatchingClock(new FakeTimeProvider());
        var state = new WatcherStateStore();
        var service = Service(store, state, time);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
            foreach (var file in files)
            {
                File.Delete(file);
            }

            await Eventually.ThatAsync(
                () =>
                {
                    time.Advance(ProcessingWatchedFolderWatcherService.TickInterval);
                    return ScanJobPayloads(store).Count > 0;
                },
                TimeSpan.FromSeconds(5));
            await AdvancePastSeveralTicksAsync(time);

            Assert.Single(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_file_moved_into_a_watched_folder_becomes_a_candidate()
    {
        // The common shape: a download client writes elsewhere and moves the finished file in.
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-2"),
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "1"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        var staging = store.Home.Join("staging");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, "Gate Test 2001.mkv");
        await File.WriteAllBytesAsync(staged, new byte[2048]);
        var libraryId = await CreateLibraryAsync(store, watched, output);

        var state = new WatcherStateStore();
        var service = Service(store, state);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // See the comment in A_file_appearing_becomes_a_candidate_without_waiting_for_the_scan_interval:
            // wait for the watcher to actually be attached instead of guessing with a fixed sleep. The move
            // is within store.Home, so it raises Renamed (not Created); the service subscribes to both.
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
            File.Move(staged, Path.Combine(watched, "Gate Test 2001.mkv"));

            await Eventually.ThatAsync(() => ScanJobPayloads(store).Count > 0);
            Assert.Single(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_growing_file_written_in_chunks_produces_one_scan_not_one_per_chunk()
    {
        // A PVR (or a downloader) writing parts of a file every so often is one arrival. The chunk writes
        // are real (proving the OS coalesces them into the debounce the way production expects), but the
        // debounce clock is fake and frozen while they happen, so every chunk lands at the same "now" -
        // there is no 1 s real-time boundary a slow CI box could straddle.
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-3"),
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "1"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output);

        var time = new TimerWatchingClock(new FakeTimeProvider());
        var state = new WatcherStateStore();
        var service = Service(store, state, time);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // See the comment in A_file_appearing_becomes_a_candidate_without_waiting_for_the_scan_interval.
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
            var target = Path.Combine(watched, "Recording.mkv");
            await using (var handle = File.Open(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                for (var i = 0; i < 10; i++)
                {
                    var chunk = new byte[4096];
                    await handle.WriteAsync(chunk);
                    await handle.FlushAsync();
                }
            }

            // Every chunk shares the same recorded "changed" instant, so exactly one tick a full debounce
            // later can possibly drain it. Advance a tick at a time while polling (rather than one big jump)
            // so this does not race the real FileSystemWatcher event, which can still take a moment to reach
            // pending.Note after the writes above return.
            await Eventually.ThatAsync(
                () =>
                {
                    time.Advance(ProcessingWatchedFolderWatcherService.TickInterval);
                    return ScanJobPayloads(store).Count > 0;
                },
                TimeSpan.FromSeconds(5));
            Assert.Single(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // --- disabled settings: no watcher --------------------------------------------------------------

    [Fact]
    public async Task A_library_with_events_switched_off_is_not_watched_but_is_reported()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-4"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output, fileSystemEventsEnabled: false);

        var time = new TimerWatchingClock(new FakeTimeProvider());
        var state = new WatcherStateStore();
        var service = Service(store, state, time);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId));
            var report = state.Reports().Single(r => r.LibraryId == libraryId);
            Assert.Equal(WatcherStatus.Disabled, report.Status);
            // Switched off deliberately is not degraded; treating it as such trains people to ignore it.
            Assert.False(report.Degraded);
            Assert.Contains("scan interval", report.Detail, StringComparison.Ordinal);

            await File.WriteAllBytesAsync(Path.Combine(watched, "no-watcher.mkv"), new byte[16]);
            // No watcher was ever created for this library, so forcing several reconcile ticks (rather than
            // hoping a fixed real-time window covered enough of them) still finds nothing to admit.
            await AdvancePastSeveralTicksAsync(time);
            Assert.Empty(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_global_kill_switch_starts_no_watcher_at_all()
    {
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-5"),
            ("WEIR_PROCESSING_WATCHER_ENABLED", "0"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);

        var time = new TimerWatchingClock(new FakeTimeProvider());
        var state = new WatcherStateStore();
        var service = Service(store, state, time);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // With the watcher switched off the service has nothing to do and ends, so no tick can ever run.
            await service.ExecuteTask!.WaitAsync(HangGuard);
            Assert.Empty(state.Reports());

            await File.WriteAllBytesAsync(Path.Combine(watched, "no-watcher.mkv"), new byte[16]);
            time.Advance(ProcessingWatchedFolderWatcherService.ReconcileInterval);
            Assert.Equal(0, time.TimersSet);
            Assert.Empty(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var (ok, detail) = state.Summary();
        Assert.True(ok);
        Assert.Contains("No workflows", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recorded_library_change_is_picked_up_without_waiting_for_the_backstop()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-9"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output, fileSystemEventsEnabled: false);
        var time = new TimerWatchingClock(new FakeTimeProvider());
        var state = new WatcherStateStore();
        var changes = new ScanSettingsChanges();
        var service = new ProcessingWatchedFolderWatcherService(
            store.Database, store.Options, new ProcessingJobStore(store.Database, time), state, Libraries,
            time, NullLogger<ProcessingWatchedFolderWatcherService>.Instance, changes);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Disabled));

            await CreateLibraryAsync(store, watched, output, fileSystemEventsEnabled: true);
            changes.Record();
            // A few ticks on the fake clock, far inside ReconcileInterval: only the recorded change can start the watcher.
            await AdvancePastSeveralTicksAsync(time);

            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Forces several reconcile ticks on a fake clock, one at a time (a single large advance only wakes the
    /// one timer already due, not the ones a tick's own continuation goes on to arm). A tick is over when the loop
    /// sets the timer for the next, so a "nothing happened" assertion reflects several ticks that genuinely ran.
    /// </summary>
    private static async Task AdvancePastSeveralTicksAsync(TimerWatchingClock time)
    {
        for (var i = 0; i < 5; i++)
        {
            var timers = time.TimersSet;
            await Eventually.ThatAdvancingAsync(time.Fake, ProcessingWatchedFolderWatcherService.TickInterval, () => time.TimersSet > timers);
        }
    }

    // --- readiness fields ----------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_lists_the_watched_library_with_the_documented_fields()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-6"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output);

        var state = new WatcherStateStore();
        var service = Service(store, state);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId && r.Status == WatcherStatus.Watching));
            var report = state.Reports().Single(r => r.LibraryId == libraryId);
            Assert.Equal("Movies", report.LibraryName);
            Assert.Equal(Path.GetFullPath(watched), Path.GetFullPath(report.WatchedFolder));
            Assert.False(report.Degraded);

            var (ok, detail) = state.Summary();
            Assert.True(ok);
            Assert.Contains("Watching 1 folder for changes", detail, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_folder_that_cannot_be_watched_falls_back_to_polling_and_does_not_throw()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-7"));
        var missing = store.Home.Join("gone");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, missing, output);

        var state = new WatcherStateStore();
        var service = Service(store, state);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId));
            var report = state.Reports().Single(r => r.LibraryId == libraryId);
            Assert.Equal(WatcherStatus.PollingFallback, report.Status);
            Assert.True(report.Degraded);
            Assert.Contains("network shares", report.Detail, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // --- overflow/error: full scan and restart (issue #552's own C#-side addition) ----------------------

    [Fact]
    public async Task An_overflow_or_error_immediately_queues_a_full_scan_and_restarts_the_watcher()
    {
        using var store = new StoreFixture(
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-8"),
            // A long debounce: if the overflow path used the ordinary debounce instead of bypassing it,
            // this test's short wait below would time out before a scan ever appeared.
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "60"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);

        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, NullLogger<ProcessingWatchedFolderWatcherService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);
            var first = service.CreatedFor(watched)[0];

            first.RaiseError(new IOException("simulated overflow"));

            await Eventually.ThatAsync(() => ScanJobPayloads(store).Count > 0);
            var body = (WireObject)WireJsonParser.Parse(ScanJobPayloads(store)[0]);
            Assert.Equal("filesystem_event", ((WireString)body.Get("scan_trigger")!).Value);

            // The watcher for this folder was replaced, not merely left running after the error.
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 2);
            Assert.NotSame(first, service.CreatedFor(watched)[1]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_folder_removed_from_inside_the_watched_folder_restarts_the_watcher_without_a_warning()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-9"));
        var watched = store.Home.Join("watch");
        Directory.CreateDirectory(Path.Combine(watched, "Release"));
        var output = store.Home.Join("out");
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);
        var log = new ListLogger<ProcessingWatchedFolderWatcherService>();

        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, log);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);
            Directory.Delete(Path.Combine(watched, "Release"));

            var removed = new DirectoryNotFoundException("Could not find a part of the path.");
            service.CreatedFor(watched)[0].RaiseError(removed);

            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 2);
            await Eventually.ThatAsync(() => ScanJobPayloads(store).Count > 0);
            Assert.DoesNotContain(log.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
            Assert.Contains(log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information && entry.Message.Contains("a folder inside", StringComparison.Ordinal));
            Assert.Contains(removed, log.Exceptions);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_access_denied_error_and_a_repeated_removal_are_warnings_even_with_the_watched_folder_there()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-11"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);
        var log = new ListLogger<ProcessingWatchedFolderWatcherService>();

        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, log);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);
            service.CreatedFor(watched)[0].RaiseError(new System.ComponentModel.Win32Exception(5, "Access is denied."));

            await Eventually.ThatAsync(() => log.Entries.Any(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 2);

            service.CreatedFor(watched)[1].RaiseError(new DirectoryNotFoundException("first"));
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 3);
            Assert.Single(log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information && entry.Message.Contains("a folder inside", StringComparison.Ordinal));

            service.CreatedFor(watched)[2].RaiseError(new DirectoryNotFoundException("second"));
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 4);
            var warnings = log.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning).ToList();
            Assert.Equal(2, warnings.Count);
            Assert.Contains("keeps being interrupted", warnings[1].Message, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_watcher_that_fails_reports_it_once_however_many_times_the_system_raises_the_error()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-12"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);
        var log = new ListLogger<ProcessingWatchedFolderWatcherService>();

        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, log);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);
            var failing = service.CreatedFor(watched)[0];

            // A network share that goes away makes Windows raise the error for one watcher twice within the same moment.
            failing.RaiseError(new System.ComponentModel.Win32Exception(5, "Access is denied."));
            failing.RaiseError(new System.ComponentModel.Win32Exception(5, "Access is denied."));

            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 2);
            Assert.Single(log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_watched_folder_that_is_itself_gone_warns_in_plain_words_and_queues_no_scan()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-10"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);
        var log = new ListLogger<ProcessingWatchedFolderWatcherService>();

        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, log);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);
            var first = service.CreatedFor(watched)[0];
            Directory.Delete(watched, recursive: true);

            first.RaiseError(new IOException("Access is denied."));

            await Eventually.ThatAsync(() => log.Entries.Any(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
            var warning = log.Entries.First(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
            Assert.Contains("can no longer see", warning.Message, StringComparison.Ordinal);
            Assert.Contains(watched, warning.Message, StringComparison.Ordinal);
            Assert.Empty(ScanJobPayloads(store));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", false)]
    public async Task A_scan_a_folder_change_queues_asks_for_passes_as_the_environment_says(string? environmentValue, bool queuesPasses)
    {
        (string, string)[] variables =
        [
            ("WEIR_CREDENTIALS_SECRET", "watcher-tests-secret-enqueue"),
            ("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", "60"),
            .. environmentValue is null ? [] : new[] { ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", environmentValue) },
        ];
        using var store = new StoreFixture(variables);
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await CreateLibraryAsync(store, watched, output);
        var service = new FakeWatcherService(store.Database, store.Options, new ProcessingJobStore(store.Database, TimeProvider.System),
            new WatcherStateStore(), Libraries, TimeProvider.System, NullLogger<ProcessingWatchedFolderWatcherService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => service.CreatedFor(watched).Count == 1);

            service.CreatedFor(watched)[0].RaiseError(new IOException("simulated overflow"));

            await Eventually.ThatAsync(() => ScanJobPayloads(store).Count > 0);
            var body = (WireObject)WireJsonParser.Parse(ScanJobPayloads(store)[0]);
            Assert.Equal(queuesPasses, ((WireBool)body.Get("enqueue_remux_jobs")!).Value);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Raises a synthetic <c>Error</c> event — a real buffer-overflow exception cannot be
    /// triggered deterministically from a test.</summary>
    private sealed class FakeFileSystemWatcher(string path) : FileSystemWatcher(path)
    {
        public void RaiseError(Exception exception) => OnError(new ErrorEventArgs(exception));
    }

    private sealed class FakeWatcherService(
        SqliteDatabase database, Weir.Core.Configuration.WeirOptions options, ProcessingJobStore jobStore, WatcherStateStore state, LibraryStore libraries,
        TimeProvider time, Microsoft.Extensions.Logging.ILogger<ProcessingWatchedFolderWatcherService> logger)
        : ProcessingWatchedFolderWatcherService(database, options, jobStore, state, libraries, time, logger)
    {
        private readonly ConcurrentDictionary<string, List<FakeFileSystemWatcher>> _created = new(StringComparer.Ordinal);

        public List<FakeFileSystemWatcher> CreatedFor(string folder)
        {
            if (!_created.TryGetValue(Path.GetFullPath(folder), out var list))
            {
                return [];
            }

            lock (list)
            {
                return list.ToList();
            }
        }

        internal override FileSystemWatcher CreateFileSystemWatcher(string folder) => new FakeFileSystemWatcher(folder);

        // Recorded on readiness, not construction: CreatedFor must never report a watcher before production
        // has subscribed to it, or a caller racing to act on "created" (as this test's overflow scenario does)
        // could act before there is anything to act on.
        internal override void OnWatcherReady(long libraryId, FileSystemWatcher watcher)
        {
            var fake = (FakeFileSystemWatcher)watcher;
            _created.AddOrUpdate(
                Path.GetFullPath(watcher.Path),
                _ => [fake],
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.Add(fake);
                        return existing;
                    }
                });
        }
    }
}
