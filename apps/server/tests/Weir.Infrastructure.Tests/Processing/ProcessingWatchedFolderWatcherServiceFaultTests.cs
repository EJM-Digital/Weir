using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>A watched folder the system will not even say is there: Weir falls back to the scan and says why.</summary>
[Collection(WriteLockTimingGroup.Name)]
public sealed class ProcessingWatchedFolderWatcherServiceFaultTests
{
    private static readonly LibraryStore Libraries = new();

    private static async Task<long> CreateLibraryAsync(StoreFixture store, string watched, string output)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
        var updated = await Libraries.UpdateAsync(uow, seeded, new ProcessingLibraryInput
        {
            Name = seeded.Name,
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = watched,
            OutputFolder = output,
            FileSystemEventsEnabled = true,
        });
        await uow.CommitAsync();
        return updated.Id;
    }

    [Fact]
    public async Task A_watched_folder_the_system_cannot_inspect_is_left_to_the_scan_and_the_reason_is_given()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "watcher-fault-secret-1"));
        var watched = store.Home.Join("watch");
        var output = store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var libraryId = await CreateLibraryAsync(store, watched, output);
        var files = new FaultingFileSystem().Fail(FileCall.DirectoryExists, FaultingFileSystem.SharingViolation, pathEndsWith: "watch");
        var time = new FakeTimeProvider();
        var state = new WatcherStateStore();
        var service = new ProcessingWatchedFolderWatcherService(
            store.Database, store.Options, new ProcessingJobStore(store.Database, time), state, Libraries, time,
            NullLogger<ProcessingWatchedFolderWatcherService>.Instance, files: files);

        WatcherReport report;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.ThatAsync(() => state.Reports().Any(r => r.LibraryId == libraryId));
            report = state.Reports().Single(r => r.LibraryId == libraryId);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(WatcherStatus.PollingFallback, report.Status);
        Assert.True(report.Degraded);
        Assert.Contains("finding new files on the scan interval instead", report.Detail, StringComparison.Ordinal);
        Assert.Contains("being used by another process", report.Detail, StringComparison.Ordinal);
        Assert.True(Directory.Exists(watched));
    }
}
