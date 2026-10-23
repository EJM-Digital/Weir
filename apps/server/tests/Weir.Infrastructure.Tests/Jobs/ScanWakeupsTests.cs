using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Security;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// A file held until a known time is looked at again at that time, not at the library's next periodic scan. Before, a
/// file whose wait had ended sat "due now" in Arriving for up to five minutes while a lane stood free.
/// </summary>
public sealed class ScanWakeupsTests
{
    private static readonly LibraryStore Libraries = new();
    private static readonly FileStateStore Files = new();

    [Fact]
    public void Keeps_the_earliest_booking_and_uses_it_up_once_due()
    {
        var wakeups = new ScanWakeups();
        var t0 = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        wakeups.Request(7, t0.AddSeconds(60));
        wakeups.Request(7, t0.AddSeconds(20));
        wakeups.Request(7, t0.AddSeconds(90));

        Assert.Equal(t0.AddSeconds(20), wakeups.BookedFor(7));
        Assert.False(wakeups.TakeDue(7, t0.AddSeconds(19)));
        Assert.True(wakeups.TakeDue(7, t0.AddSeconds(20)));
        Assert.False(wakeups.TakeDue(7, t0.AddSeconds(21)));
        Assert.Null(wakeups.BookedFor(7));
    }

    [Fact]
    public void The_next_look_is_the_earlier_of_a_booked_look_and_the_next_periodic_scan()
    {
        var looks = new ScanWakeups();
        var t0 = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(looks.NextLookFor(3));
        looks.RecordNextPeriodic(3, t0.AddMinutes(5));
        Assert.Equal(t0.AddMinutes(5), looks.NextLookFor(3));
        looks.Request(3, t0.AddSeconds(40));
        Assert.Equal(t0.AddSeconds(40), looks.NextLookFor(3));
        looks.RecordNextPeriodic(3, t0.AddSeconds(10));
        Assert.Equal(t0.AddSeconds(10), looks.NextLookFor(3));
        looks.ForgetPeriodic(3);
        Assert.Equal(t0.AddSeconds(40), looks.NextLookFor(3));
    }

    /// <summary>A Movies workflow over an empty watched folder, and a way to run its scan with a handler that reports to the wake-ups.</summary>
    private sealed record ScanSetup(long LibraryId, string WatchedFolder, Func<Task> ScanAsync);

    private static async Task<ScanSetup> ScanSetupAsync(StoreFixture store, ScanWakeups wakeups, long readyAfterSeconds)
    {
        store.Clock.Set(DateTimeOffset.UtcNow);
        var cipher = new CredentialCipher(store.Options.CredentialsSecret, store.Options.SessionSecret, store.Options.PreviousCredentialsSecrets, store.Clock);
        var connections = new MediaManagerConnectionService(store.Options, cipher, new HttpMediaManagerPorts(new FakeManagerHttp()), new MediaManagerConnectionStore());
        var jobs = new ProcessingJobStore(store.Database, store.Clock);
        var handler = new ProcessingWatchedFolderScanDispatchJobHandler(
            store.Database, store.Clock, store.Options, jobs, connections, new SuiteSettingsStore(new AuthStore()), Libraries, Files, new FileSkipMarkerStore(), wakeups);
        await store.Execute(
            "INSERT INTO operator_settings (id, minimum_free_disk_space_mb) " +
            "VALUES (1, 0) ON CONFLICT(id) DO UPDATE SET minimum_free_disk_space_mb = 0");
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);

        long libraryId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            libraryId = (await Libraries.CreateAsync(uow, new ProcessingLibraryInput
            {
                Name = "Movies wake-up",
                MediaType = ProcessingMediaScopes.Movie,
                WatchedFolder = watched,
                OutputFolder = output,
                ReadyAfterSeconds = readyAfterSeconds,
                MinFileSizeMb = 0,
            })).Id;
            await uow.CommitAsync();
        }

        var scans = 0;
        return new ScanSetup(libraryId, watched, async () =>
        {
            var payload = new WireObject().Set("enqueue_remux_jobs", true).Set("scan_trigger", "manual").Set("media_scope", "movie").Set("library_id", libraryId);
            var job = await jobs.EnqueueOrGetAsync($"scan-wakeup-{scans++}", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
            await handler.HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "test"), CancellationToken.None);
        });
    }

    [Fact]
    public async Task A_scan_that_holds_a_file_books_the_next_look_for_when_the_hold_ends()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "wakeup-tests-credentials-secret"));
        var wakeups = new ScanWakeups();
        var setup = await ScanSetupAsync(store, wakeups, readyAfterSeconds: 60);
        File.WriteAllBytes(Path.Combine(setup.WatchedFolder, "Fresh Download 2026.mkv"), [1]);
        var libraryId = setup.LibraryId;

        await setup.ScanAsync();

        // Too new to touch, so it is on hold until a known time (first while its size settles, then its last-changed age), and
        // the next look is booked a second after that hold ends.
        Assert.Equal("on_hold", await StatusAsync(store, "Fresh Download 2026.mkv"));
        var holdUntil = await HoldUntilAsync(store, "Fresh Download 2026.mkv");
        Assert.NotNull(holdUntil);
        Assert.True(holdUntil > store.Clock.GetUtcNow());
        var booked = wakeups.BookedFor(libraryId);
        Assert.NotNull(booked);
        Assert.InRange((booked.Value - holdUntil.Value).TotalSeconds, 0.5, 1.5);
    }

    [Fact]
    public async Task A_scan_books_the_next_look_for_when_a_failed_files_retry_falls_due()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "wakeup-tests-retry-secret"));
        var wakeups = new ScanWakeups();
        var setup = await ScanSetupAsync(store, wakeups, readyAfterSeconds: 0);
        File.WriteAllBytes(Path.Combine(setup.WatchedFolder, "Failed Download 2026.mkv"), [1]);
        var retryAt = store.Clock.GetUtcNow().AddMinutes(5);
        await store.Execute(
            "INSERT INTO files (library_id, relative_path, status, size_bytes, failure_class, failure_attempts, next_retry_at) " +
            $"VALUES ({setup.LibraryId}, 'Failed Download 2026.mkv', 'processing_failed', 1, 'execution', 1, '{TimestampColumns.Orm(retryAt)}')");

        await setup.ScanAsync();

        Assert.Equal("processing_failed", await StatusAsync(store, "Failed Download 2026.mkv"));
        var booked = wakeups.BookedFor(setup.LibraryId);
        Assert.NotNull(booked);
        Assert.InRange((booked.Value - retryAt).TotalSeconds, 0.5, 1.5);
    }

    [Fact]
    public async Task A_scan_after_a_failed_files_retry_is_due_queues_the_retry_and_books_nothing()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "wakeup-tests-due-retry-secret"));
        var wakeups = new ScanWakeups();
        var setup = await ScanSetupAsync(store, wakeups, readyAfterSeconds: 0);
        File.WriteAllBytes(Path.Combine(setup.WatchedFolder, "Failed Download 2026.mkv"), [1]);
        var retryAt = store.Clock.GetUtcNow().AddMinutes(-1);
        await store.Execute(
            "INSERT INTO files (library_id, relative_path, status, size_bytes, failure_class, failure_attempts, next_retry_at) " +
            $"VALUES ({setup.LibraryId}, 'Failed Download 2026.mkv', 'processing_failed', 1, 'execution', 1, '{TimestampColumns.Orm(retryAt)}')");

        await setup.ScanAsync();

        Assert.Equal(1, await store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Null(wakeups.BookedFor(setup.LibraryId));
    }

    [Fact]
    public async Task The_scan_timer_looks_as_soon_as_a_booking_is_due_rather_than_at_its_next_interval()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "wakeup-tests-schedule-secret"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        long libraryId;
        await using (var uow = await UnitOfWork.OpenAsync(store.Database))
        {
            var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
            libraryId = (await Libraries.UpdateAsync(uow, seeded, new ProcessingLibraryInput
            {
                Name = seeded.Name,
                MediaType = ProcessingMediaScopes.Movie,
                WatchedFolder = watched,
                OutputFolder = output,
                ScanIntervalSeconds = 300,
            })).Id;
            await uow.CommitAsync();
        }

        var wakeups = new ScanWakeups();
        var task = new ProcessingWatchedFolderScanDispatchScheduleTask(
            store.Database, store.Options, new ProcessingJobStore(store.Database, store.Clock), new OperatorSettingsStore(), Libraries, store.Clock,
            NullLogger<ProcessingWatchedFolderScanDispatchScheduleTask>.Instance, wakeups);
        const string countMovieScans =
            "SELECT count(*) FROM jobs WHERE job_kind = 'processing.watched_folder.remux_scan_dispatch.v1' AND payload_json LIKE '%\"media_scope\":\"movie\"%'";

        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await store.Scalar(countMovieScans));
        // The timer says when it will look next, for the screen to count down to.
        Assert.Equal(store.Clock.GetUtcNow().AddSeconds(300), wakeups.NextLookFor(libraryId));
        await store.Execute("UPDATE jobs SET status = 'completed'");

        // Twenty seconds on, with the next periodic scan four and a half minutes away: nothing, until a look is due.
        store.Clock.Set(store.Clock.GetUtcNow().AddSeconds(20));
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await store.Scalar(countMovieScans));

        wakeups.Request(libraryId, store.Clock.GetUtcNow());
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await store.Scalar(countMovieScans));
        Assert.Null(wakeups.BookedFor(libraryId));
    }

    private static async Task<DateTimeOffset?> HoldUntilAsync(StoreFixture store, string relative)
    {
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hold_until FROM files WHERE relative_path = $p";
        command.Parameters.AddWithValue("$p", relative);
        return TimestampColumns.Parse(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> StatusAsync(StoreFixture store, string relative)
    {
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM files WHERE relative_path = $p";
        command.Parameters.AddWithValue("$p", relative);
        return await command.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task A_scan_over_several_failed_files_books_the_next_look_for_the_earliest_retry()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "wakeup-tests-earliest-secret"));
        var wakeups = new ScanWakeups();
        var setup = await ScanSetupAsync(store, wakeups, readyAfterSeconds: 0);
        var now = store.Clock.GetUtcNow();
        // Walked in path order: the second retry is later than the first, and the third is earlier than both.
        foreach (var (name, hours) in new[] { ("A.mkv", 2), ("B.mkv", 3), ("C.mkv", 1) })
        {
            File.WriteAllBytes(Path.Combine(setup.WatchedFolder, name), [1]);
            await store.Execute(
                "INSERT INTO files (library_id, relative_path, status, size_bytes, failure_class, failure_attempts, next_retry_at) " +
                $"VALUES ({setup.LibraryId}, '{name}', 'processing_failed', 1, 'execution', 1, '{TimestampColumns.Orm(now.AddHours(hours))}')");
        }

        await setup.ScanAsync();

        var booked = wakeups.BookedFor(setup.LibraryId);
        Assert.NotNull(booked);
        Assert.InRange((booked.Value - now.AddHours(1)).TotalSeconds, 0.5, 1.5);
    }
}
