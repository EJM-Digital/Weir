using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// "Keep looking for new files while paused" lets a scan run through a pause, and a scan may only look: it records what it
/// finds and queues passes, but removes, deletes and writes nothing. Whatever it left undone waits for the first scan after
/// the pause ends.
/// </summary>
public sealed class ScanWhilePausedTests
{
    private const string RemuxJobs = "SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'";

    private static readonly FileStateStore Files = new();

    private static Task SetPausedAsync(StoreFixture store, bool paused) =>
        store.Execute($"UPDATE suite_settings SET processing_paused = {(paused ? 1 : 0)}, processing_paused_until = NULL, scan_while_paused = 1 WHERE id = 1");

    private static async Task<ProcessingFileRecord?> RowAsync(StoreFixture store, long libraryId, string relativePath)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        return await Files.FindAsync(uow, libraryId, relativePath);
    }

    [Fact]
    public async Task A_scan_does_not_finish_removing_a_cleaned_movies_original_while_paused()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        var release = Path.Combine(watched, "Film 2024");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(output);
        var source = Path.Combine(release, "Film 2024.mkv");
        await File.WriteAllBytesAsync(source, "the original download"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(release, "Film 2024.nfo"), "notes");
        var cleaned = Path.Combine(output, "Film 2024.mkv");
        await File.WriteAllBytesAsync(cleaned, "the cleaned movie"u8.ToArray());
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(store, watched, output, removeOriginalAfterSuccess: true);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.WriteCompletedPassEventAsync(store, libraryId, "Film 2024/Film 2024.mkv", "movie", source, cleaned);
        await SetPausedAsync(store, true);

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.True(File.Exists(source), "the original must stay while processing is paused");
        Assert.True(File.Exists(Path.Combine(release, "Film 2024.nfo")));
        Assert.True(File.Exists(cleaned));
        Assert.Equal(0, await store.Scalar(RemuxJobs));

        // Resumed, the next scan finishes the removal, and the movie is never cleaned again.
        await SetPausedAsync(store, false);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.False(Directory.Exists(release));
        Assert.True(File.Exists(cleaned));
        Assert.Equal(0, await store.Scalar(RemuxJobs));
    }

    [Fact]
    public async Task A_scan_records_a_new_file_as_waiting_while_paused_and_queues_it_once_after()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Film.2024.mkv");
        await File.WriteAllBytesAsync(source, "a new download"u8.ToArray());
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(store, watched, output);
        await SetPausedAsync(store, true);

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var waiting = await RowAsync(store, libraryId, "Film.2024.mkv");
        Assert.Equal(ProcessingFileStatuses.OutOfSchedule, waiting!.Status);
        Assert.Equal(0, await store.Scalar(RemuxJobs));
        Assert.True(File.Exists(source));

        await SetPausedAsync(store, false);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.Equal(1, await store.Scalar(RemuxJobs));
    }

    [Fact]
    public async Task A_scan_through_a_pause_that_ends_at_a_set_time_records_the_file_as_waiting()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(watched, "Film.2024.mkv"), "a new download"u8.ToArray());
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(store, watched, output);
        await store.Execute("UPDATE suite_settings SET processing_paused = 1, processing_paused_until = '2099-01-01T00:00:00', scan_while_paused = 1 WHERE id = 1");

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.Equal(ProcessingFileStatuses.OutOfSchedule, (await RowAsync(store, libraryId, "Film.2024.mkv"))!.Status);
        Assert.Equal(0, await store.Scalar(RemuxJobs));
    }

    [Fact]
    public async Task A_file_waiting_through_a_pause_that_is_deleted_is_held_as_gone_once_the_scan_sees_it()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var source = Path.Combine(watched, "Film.2024.mkv");
        await File.WriteAllBytesAsync(source, "a new download"u8.ToArray());
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(store, watched, output);
        await SetPausedAsync(store, true);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);
        Assert.Equal(ProcessingFileStatuses.OutOfSchedule, (await RowAsync(store, libraryId, "Film.2024.mkv"))!.Status);

        File.Delete(source);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        var gone = await RowAsync(store, libraryId, "Film.2024.mkv");
        Assert.True(GoneSourceText.IsHeld(gone!.Status, gone.StatusReason));
        Assert.NotNull(gone.HoldUntil);
        Assert.Equal(1, await store.Scalar("SELECT COUNT(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder'"));
        Assert.Equal(0, await store.Scalar(RemuxJobs));
    }

    [Fact]
    public async Task A_scan_does_not_delete_a_rejected_file_while_paused()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var sample = Path.Combine(watched, "Film.2024.sample.mkv");
        await File.WriteAllBytesAsync(sample, [1]);
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(
            store, watched, output, rejectedFileAction: "delete_file", minFileSizeMb: 100);
        await SetPausedAsync(store, true);

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.True(File.Exists(sample), "a rejected file is deleted only when Weir is working");

        await SetPausedAsync(store, false);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        Assert.False(File.Exists(sample));
    }
}
