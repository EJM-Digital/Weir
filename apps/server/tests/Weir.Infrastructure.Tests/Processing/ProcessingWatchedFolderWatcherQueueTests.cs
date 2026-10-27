using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Which workflows a change in a watched folder queues a scan for, and how many times one is asked for.</summary>
public sealed class ProcessingWatchedFolderWatcherQueueTests
{
    private const string ScanJobs = "SELECT COUNT(*) FROM jobs WHERE job_kind = 'processing.watched_folder.remux_scan_dispatch.v1'";

    private static readonly LibraryStore Libraries = new();

    private static ProcessingWatchedFolderWatcherService ServiceFor(StoreFixture store)
    {
        var time = new FakeTimeProvider();
        return new ProcessingWatchedFolderWatcherService(
            store.Database, store.Options, new ProcessingJobStore(store.Database, time), new WatcherStateStore(), Libraries, time,
            NullLogger<ProcessingWatchedFolderWatcherService>.Instance);
    }

    private static async Task<long> WatchedMovieLibraryAsync(StoreFixture store)
    {
        var watched = store.Home.Join("watch");
        Directory.CreateDirectory(watched);
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
        var updated = await Libraries.UpdateAsync(uow, seeded, new ProcessingLibraryInput
        {
            Name = seeded.Name,
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = watched,
            OutputFolder = store.Home.Join("out"),
        });
        await uow.CommitAsync();
        return updated.Id;
    }

    [Fact]
    public async Task A_workflow_that_is_gone_or_switched_off_is_not_scanned_and_one_that_is_on_is()
    {
        using var store = new StoreFixture();
        var enabled = await WatchedMovieLibraryAsync(store);
        await store.Execute("INSERT INTO libraries (name, media_type, enabled) VALUES ('Off', 'tv', 0)");
        var off = await store.Scalar("SELECT id FROM libraries WHERE name = 'Off'");

        await ServiceFor(store).EnqueueScansAsync([9999, off, enabled], CancellationToken.None);

        Assert.Equal(1, await store.Scalar(ScanJobs));
    }

    [Fact]
    public void A_workflow_asked_about_twice_is_listed_once_in_the_order_it_was_first_asked_about()
    {
        var queue = new ConcurrentQueue<long>([3, 1, 3, 2, 1]);

        Assert.Equal([3, 1, 2], ProcessingWatchedFolderWatcherService.DrainAll(queue));
        Assert.Empty(ProcessingWatchedFolderWatcherService.DrainAll(queue));
    }
}
