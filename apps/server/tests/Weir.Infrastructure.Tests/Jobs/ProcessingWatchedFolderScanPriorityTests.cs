namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// A workflow's priority (Settings › Workflows, "Priority") is what its queued work is ordered by: a scan gives each pass it queues the
/// priority of its workflow, and the queue hands out the higher number first (ProcessingJobStoreTests.Claim_order_is_priority_then_id).
/// </summary>
public sealed class ProcessingWatchedFolderScanPriorityTests
{
    private const string Film = "Film 2001.mkv";

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-5)]
    public async Task A_scan_queues_each_pass_with_the_priority_of_its_workflow(int priority)
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { Priority = priority });
        world.Write(Film);

        await world.ScanAsync(workflow);

        Assert.Equal([Film], await world.QueuedPathsAsync());
        Assert.Equal(priority, await world.Store.Scalar("SELECT priority FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }
}
