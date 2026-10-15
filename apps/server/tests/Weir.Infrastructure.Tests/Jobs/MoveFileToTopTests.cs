using Weir.Core.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// Raising one file's waiting pass above the rest: the pass is found by workflow and path, and found and raised in one step, so a
/// pass a worker has already claimed is never raised.
/// </summary>
public sealed class MoveFileToTopTests : IDisposable
{
    private const string Kind = "processing.file.remux_pass.v1";
    private const string Path = "Heat (1995)/heat.mkv";
    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;
    private readonly JobsTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private Task<ProcessingJob> QueuePassAsync(long libraryId, string path) =>
        _db.Store.EnqueueOrGetAsync(
            $"{Kind}:{libraryId}:{path}",
            Kind,
            $$"""{"relative_media_path": "{{path}}", "media_scope": "movie", "library_id": {{libraryId}}}""");

    private async Task<List<long>> ClaimOrderAsync()
    {
        var order = new List<long>();
        while (await _db.Store.ClaimNextAsync("worker", T0.AddHours(1), T0) is { } claimed)
        {
            order.Add(claimed.Id);
        }

        return order;
    }

    [Fact]
    public async Task Two_workflows_holding_the_same_path_are_raised_one_at_a_time()
    {
        var first = await QueuePassAsync(1, Path);
        var second = await QueuePassAsync(2, Path);
        var other = await QueuePassAsync(1, "Up (2009)/up.mkv");

        Assert.Equal(JobActionOutcome.Ok, await _db.Store.MoveFileToTopAsync(2, Path));

        Assert.Equal([second.Id, first.Id, other.Id], await ClaimOrderAsync());
    }

    [Fact]
    public async Task A_workflow_with_no_pass_waiting_for_the_path_raises_nothing()
    {
        var first = await QueuePassAsync(1, Path);
        var other = await QueuePassAsync(1, "Up (2009)/up.mkv");

        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.MoveFileToTopAsync(2, Path));

        Assert.Equal([first.Id, other.Id], await ClaimOrderAsync());
        Assert.Equal(0, _db.Count("SELECT max(priority) FROM jobs"));
    }

    [Fact]
    public async Task A_pass_a_worker_has_already_claimed_is_not_raised()
    {
        var running = await QueuePassAsync(1, Path);
        var waiting = await QueuePassAsync(1, "Up (2009)/up.mkv");
        Assert.Equal(running.Id, (await _db.Store.ClaimNextAsync("worker", T0.AddHours(1), T0))!.Id);

        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.MoveFileToTopAsync(1, Path));

        Assert.Equal(0, _db.Count("SELECT max(priority) FROM jobs"));
        Assert.Equal(waiting.Id, (await _db.Store.ClaimNextAsync("worker", T0.AddHours(1), T0))!.Id);
    }

    [Fact]
    public async Task A_pass_queued_without_a_workflow_is_not_raised_for_any_workflow()
    {
        await _db.Store.EnqueueOrGetAsync("unassigned", Kind, $$"""{"relative_media_path": "{{Path}}", "media_scope": "movie"}""");

        Assert.Equal(JobActionOutcome.NotFound, await _db.Store.MoveFileToTopAsync(1, Path));
    }
}
