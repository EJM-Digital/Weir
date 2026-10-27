using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A scan of a workflow whose schedule is closed records what it finds as waiting and queues nothing until the window opens.</summary>
public sealed class ScanOutsideWindowTests
{
    private static readonly FileStateStore Files = new();

    [Fact]
    public async Task A_scan_outside_the_workflows_window_records_a_new_file_as_waiting_and_queues_nothing()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(watched, "Film.2024.mkv"), "a new download"u8.ToArray());
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(store, watched, output);
        await store.Execute($"UPDATE libraries SET schedule_enabled = 1, schedule_grid = '{new string('0', ScheduleGrid.SlotsPerWeek)}' WHERE id = {libraryId}");

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, jobs, libraryId, enqueueRemuxJobs: true);

        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var waiting = await Files.FindAsync(uow, libraryId, "Film.2024.mkv");
        Assert.Equal(ProcessingFileStatuses.OutOfSchedule, waiting!.Status);
        Assert.Equal(0, await store.Scalar("SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }
}
