using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// Every job kind that can change a media file (<c>docs/coverage/file-changing-paths.json</c>) is held back by the pause: with processing
/// paused a worker on the lane that runs the kind leases nothing, the job stays queued with its attempt untouched, and the file its
/// handler would change is left exactly as it was. Resumed, the handler runs once. Each handler here changes one real file when it runs,
/// so "untouched" is the file itself, not a flag.
/// </summary>
public sealed class PauseGatesTests : IDisposable
{
    private readonly JobsTestDatabase _db = new();

    public PauseGatesTests()
    {
        _db.SeedSuiteSettings();
    }

    public static TheoryData<string, bool> HeldKinds()
    {
        var data = new TheoryData<string, bool>();
        foreach (var path in FileChangingPathsTable.Paths.Where(path => path.JobKind is not null))
        {
            foreach (var scanWhilePaused in new[] { true, false })
            {
                // The one scan that keeps looking for files runs through a pause when asked to; its proof is that it changes nothing.
                if (path.Gate != PauseGate.Scan || !scanWhilePaused)
                {
                    data.Add(path.JobKind!, scanWhilePaused);
                }
            }
        }

        return data;
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(HeldKinds))]
    public async Task A_paused_worker_leaves_the_job_queued_and_the_file_untouched_and_runs_it_once_when_resumed(string jobKind, bool scanWhilePaused)
    {
        var mediaFile = _db.Join("film.mkv");
        await File.WriteAllTextAsync(mediaFile, "the original");
        var handlers = FileChangingPathsTable.Paths.Where(path => path.JobKind is not null)
            .Select(path => new DelegateHandler(path.JobKind!, _ => File.AppendAllText(mediaFile, " changed by " + path.JobKind)))
            .ToList();
        var registry = new JobHandlerRegistry(handlers);
        var lane = WorkLanes.LaneOf(jobKind);
        var kinds = lane == WorkLane.Upkeep ? WorkLanes.UpkeepLaneKinds(registry) : WorkLanes.FilesLaneKinds(registry);
        var processor = _db.Processor(handlers);
        await _db.Store.EnqueueOrGetAsync("held", jobKind);
        _db.Pause(scanWhilePaused);

        var paused = await processor.ProcessOneAsync("worker", 3600, lane: lane, kinds: kinds);

        Assert.Equal(JobProcessOutcome.Idle, paused);
        Assert.Equal("the original", await File.ReadAllTextAsync(mediaFile));
        var queued = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, queued.Status);
        Assert.Equal(0, queued.AttemptCount);
        Assert.Null(queued.LeaseOwner);

        _db.Execute("UPDATE suite_settings SET processing_paused = 0 WHERE id = 1");
        var resumed = await processor.ProcessOneAsync("worker", 3600, lane: lane, kinds: kinds);

        Assert.Equal(JobProcessOutcome.Processed, resumed);
        Assert.Equal("the original changed by " + jobKind, await File.ReadAllTextAsync(mediaFile));
        Assert.Equal(ProcessingJobStatus.Completed, (await _db.Store.GetAsync(1))!.Status);
        Assert.Equal(JobProcessOutcome.Idle, await processor.ProcessOneAsync("worker", 3600, lane: lane, kinds: kinds));
        Assert.Equal("the original changed by " + jobKind, await File.ReadAllTextAsync(mediaFile));
    }
}
