using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Activity;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// The queue tells the live stream when it changes, once the change is committed, so Dashboard's "Files at once", the
/// maintenance panel and the Working badge follow it without a timer.
/// </summary>
public sealed class QueueChangeAnnouncementTests : IDisposable
{
    private const string FilePass = "processing.file.remux_pass.v1";
    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;

    private readonly JobsTestDatabase _db = new();
    private readonly DataChangePublisher _changes = new();
    private readonly PublishedTopics _published;
    private readonly ProcessingJobStore _store;

    public QueueChangeAnnouncementTests()
    {
        _store = new ProcessingJobStore(_db.Database, _db.Clock, changes: _changes);
        _published = new PublishedTopics(_changes);
    }

    public void Dispose()
    {
        _published.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_file_pass_queued_moves_the_jobs_and_files_at_once()
    {
        await _store.EnqueueOrGetAsync("pass", FilePass);

        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());
    }

    [Fact]
    public async Task A_maintenance_sweep_moves_the_maintenance_panel_and_not_files_at_once()
    {
        await _store.EnqueueNextRunAsync("sweep", PeriodicJobKinds.WorkTempStaleSweep);

        Assert.Equal([DataTopics.Jobs, DataTopics.Maintenance], await _published.TakeAsync());
    }

    [Fact]
    public async Task A_scan_moves_only_the_jobs_because_upkeep_is_exempt_from_files_at_once()
    {
        await _store.EnqueueOrGetAsync("scan", ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch);

        Assert.Equal([DataTopics.Jobs], await _published.TakeAsync());
    }

    [Fact]
    public async Task Asking_for_a_job_that_is_already_queued_changes_nothing()
    {
        await _store.EnqueueOrGetAsync("pass", FilePass);
        await _published.TakeAsync();

        await _store.EnqueueOrGetAsync("pass", FilePass);

        Assert.Empty(await _published.TakeAsync());
    }

    [Fact]
    public async Task A_worker_looking_for_work_and_finding_none_announces_nothing()
    {
        Assert.Null(await _store.ClaimNextAsync("worker", T0.AddHours(1), T0));

        Assert.Empty(await _published.TakeAsync());
    }

    [Fact]
    public async Task Starting_finishing_and_failing_a_pass_each_announce()
    {
        var finished = await _store.EnqueueOrGetAsync("finished", FilePass);
        var failed = await _store.EnqueueOrGetAsync("failed", FilePass, maxAttempts: 1);
        await _published.TakeAsync();

        Assert.NotNull(await _store.ClaimNextAsync("worker", T0.AddHours(1), T0));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());

        Assert.True(await _store.CompleteClaimedAsync(finished.Id, "worker", T0));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());

        Assert.NotNull(await _store.ClaimNextAsync("worker", T0.AddHours(1), T0));
        await _published.TakeAsync();
        Assert.True(await _store.FailClaimedAsync(failed.Id, "worker", "gave up", T0));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());
    }

    [Fact]
    public async Task Putting_a_claimed_pass_back_and_cancelling_a_queued_one_announce()
    {
        var held = await _store.EnqueueOrGetAsync("held", FilePass);
        var cancelled = await _store.EnqueueOrGetAsync("cancelled", FilePass);
        await _published.TakeAsync();
        await _store.ClaimNextAsync("worker", T0.AddHours(1), T0);
        await _published.TakeAsync();

        Assert.True(await _store.ReleaseClaimedAsync(held.Id, "worker", T0));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());

        Assert.Equal(JobActionOutcome.Ok, await _store.CancelPendingAsync(cancelled.Id));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());
    }

    [Fact]
    public async Task Raising_a_queued_pass_announces_and_renewing_a_lease_does_not()
    {
        var running = await _store.EnqueueOrGetAsync("running", FilePass);
        var waiting = await _store.EnqueueOrGetAsync("waiting", FilePass);
        await _store.ClaimNextAsync("worker", T0.AddHours(1), T0);
        await _published.TakeAsync();

        Assert.True(await _store.RenewLeaseAsync(running.Id, "worker", T0.AddHours(2), T0));
        Assert.Empty(await _published.TakeAsync());

        Assert.Equal(JobActionOutcome.Ok, await _store.MoveToTopAsync(waiting.Id));
        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());
    }

    [Fact]
    public async Task A_unit_of_work_that_queues_several_passes_announces_once_after_it_commits()
    {
        await using var unit = await UnitOfWork.OpenAsync(_db.Database);
        var transaction = await unit.WriteTransactionAsync();
        foreach (var key in new[] { "a", "b", "c" })
        {
            _store.EnqueueOrGet(unit.Connection, transaction, key, FilePass, payloadJson: null, maxAttempts: 3, runnerCost: 0, priority: 0);
        }

        Assert.Empty(await _published.TakeAsync());

        await unit.CommitAsync();

        Assert.Equal([DataTopics.Jobs, DataTopics.FilesAtOnce], await _published.TakeAsync());
    }

    [Fact]
    public async Task A_unit_of_work_that_is_rolled_back_announces_nothing()
    {
        await using (var unit = await UnitOfWork.OpenAsync(_db.Database))
        {
            _store.EnqueueOrGet(unit.Connection, await unit.WriteTransactionAsync(), "undone", FilePass, payloadJson: null, maxAttempts: 3, runnerCost: 0, priority: 0);
            await unit.RollbackAsync();
        }

        Assert.Empty(await _published.TakeAsync());
    }

    [Fact]
    public async Task A_transaction_no_unit_of_work_began_announces_nothing()
    {
        using var connection = _db.Database.Open();
        using var transaction = connection.BeginTransaction();

        _store.AnnounceQueueChange(transaction, FilePass);
        transaction.Commit();

        Assert.Empty(await _published.TakeAsync());
    }
}
