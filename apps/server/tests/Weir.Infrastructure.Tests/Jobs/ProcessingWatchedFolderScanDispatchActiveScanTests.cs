using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Whether a scan already waiting covers a workflow: by its own id, or, for a scan queued before workflows had ids, by Movies or TV.</summary>
public sealed class ProcessingWatchedFolderScanDispatchActiveScanTests
{
    [Theory]
    [InlineData(null, "movie", null, true)]
    [InlineData("", "movie", null, true)]
    [InlineData("not json", "movie", null, true)]
    [InlineData("not json", "tv", null, false)]
    [InlineData("not json", "movie", 3L, true)]
    [InlineData("[1]", "movie", null, true)]
    [InlineData("""{"media_scope":"tv"}""", "tv", null, true)]
    [InlineData("""{"media_scope":"tv"}""", "movie", null, false)]
    [InlineData("""{"media_scope":"anime"}""", "movie", null, true)]
    [InlineData("""{"media_scope":"tv"}""", "tv", 5L, true)]
    [InlineData("""{"media_scope":"tv"}""", "movie", 5L, false)]
    [InlineData("""{"library_id":7}""", "movie", 7L, true)]
    [InlineData("""{"library_id":7}""", "movie", 8L, false)]
    [InlineData("""{"library_id":7}""", "movie", null, true)]
    [InlineData("""{"library_id":0}""", "movie", 8L, true)]
    [InlineData("""{"library_id":"7"}""", "movie", 7L, true)]
    public async Task A_waiting_scan_covers_the_workflow_it_names_or_the_scope_it_was_queued_for(string? payload, string scope, long? libraryId, bool covered)
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "active-scan-secret"));
        var jobs = new ProcessingJobStore(store.Database, store.Clock);
        await jobs.EnqueueOrGetAsync("waiting", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, payload, maxAttempts: 3);

        await using var uow = await UnitOfWork.OpenAsync(store.Database);

        Assert.Equal(covered, await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, scope, libraryId));
    }

    [Fact]
    public async Task With_no_scan_waiting_nothing_is_covered()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "active-scan-secret-2"));

        await using var uow = await UnitOfWork.OpenAsync(store.Database);

        Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "movie", null));
        Assert.False(await ProcessingWatchedFolderScanDispatchEnqueue.QueueHasActiveScanAsync(uow, "tv", 4));
    }
}
