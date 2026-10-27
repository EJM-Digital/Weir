using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Putting a file back to work by hand: what stops it, and the hand-off it keeps when it goes back.</summary>
public sealed class RequeueStoreWorkflowTests : IDisposable
{
    private const string Relative = "Film/film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly RequeueStore _requeue;
    private long _library;

    public RequeueStoreWorkflowTests() => _requeue = new RequeueStore(_fixture.Jobs, _fixture.Libraries);

    public void Dispose() => _fixture.Dispose();

    private async Task<ProcessingFileRecord> RowAsync(string status, string relative = Relative, long? library = null)
    {
        _library = library ?? await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("watched"));
        return new ProcessingFileRecord { Id = 1, LibraryId = _library, RelativePath = relative, Status = status };
    }

    private Task<Weir.Core.Jobs.ProcessingJob> QueueEarlierPassAsync(string payload) =>
        _fixture.Jobs.EnqueueOrGetAsync("earlier-pass", RequeueStore.RemuxPassJobKind, payload);

    private static string Payload(long library, string origin) =>
        $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","library_id":{{{library}}},"origin":{{{origin}}}}""";

    [Fact]
    public async Task A_file_whose_workflow_was_removed_is_not_queued_and_the_reason_is_given()
    {
        var row = await RowAsync(ProcessingFileStatuses.ProcessingFailed, library: 9999);

        var result = await _fixture.Db(uow => _requeue.RequeueFileAsync(uow, row));

        Assert.Equal((0, 1), (result.Requeued, result.Skipped));
        Assert.Equal("The workflow this file belonged to no longer exists, so there is nowhere to queue it.", result.Detail);
        Assert.False(await _fixture.Db(uow => _requeue.CanRequeueAsync(uow, row), commit: false));
    }

    [Fact]
    public async Task A_show_put_back_by_hand_is_queued_as_a_show()
    {
        var tv = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("shows"));
        var row = await RowAsync(ProcessingFileStatuses.ProcessingFailed, "Show/S01/e01.mkv", library: tv);

        var result = await _fixture.Db(uow => _requeue.RequeueFileAsync(uow, row));

        Assert.Equal(1, result.Requeued);
        var queued = (await _fixture.Jobs.ListAsync()).Single(job => job.DedupeKey.Contains(":requeue:", StringComparison.Ordinal));
        Assert.Contains("\"media_scope\":\"tv\"", queued.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_file_whose_path_leaves_the_watched_folder_has_no_original_to_queue()
    {
        var row = await RowAsync(ProcessingFileStatuses.Processed, relative: "../outside.mkv");

        var result = await _fixture.Db(uow => _requeue.RequeueFileAsync(uow, row));

        Assert.Equal((0, 1, RequeueStore.OriginalGoneDetail), (result.Requeued, result.Skipped, result.Detail));
    }

    [Fact]
    public async Task A_file_put_back_by_hand_keeps_the_hand_off_it_came_with_so_the_manager_still_hears_the_outcome()
    {
        var row = await RowAsync(ProcessingFileStatuses.ProcessingFailed);
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", _library, Relative);
            return 0;
        });
        await QueueEarlierPassAsync(Payload(_library, """{"source_key":"deluno","handoff_id":"h1","callback_path":"/api/integrations/processors/events"}"""));

        var result = await _fixture.Db(uow => _requeue.RequeueFileAsync(uow, row));

        Assert.Equal(1, result.Requeued);
        var queued = (await _fixture.Jobs.ListAsync()).Single(job => job.DedupeKey.Contains(":requeue:", StringComparison.Ordinal));
        var origin = (WireObject)((WireObject)WireJsonParser.Parse(queued.PayloadJson!))["origin"];
        Assert.Equal("h1", ((WireString)origin["handoff_id"]).Value);
    }

    [Theory]
    [InlineData("""{"relative_media_path":"Other/other.mkv","origin":{"source_key":"deluno","handoff_id":"h1"}}""")]
    [InlineData("""{"relative_media_path":"Film/film.mkv","library_id":987654,"origin":{"source_key":"deluno","handoff_id":"h1"}}""")]
    [InlineData("""{"relative_media_path":"Film/film.mkv","origin":"deluno"}""")]
    [InlineData("""{"relative_media_path":"Film/film.mkv","origin":{"source_key":"deluno"}}""")]
    [InlineData("""{"relative_media_path":"Film/film.mkv","origin":{"source_key":"deluno","handoff_id":"never-received"}}""")]
    public async Task A_pass_that_is_not_this_files_hand_off_gives_nothing_to_carry(string payload)
    {
        var row = await RowAsync(ProcessingFileStatuses.ProcessingFailed);
        await QueueEarlierPassAsync(payload);

        var origin = await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, row.LibraryId, row.RelativePath), commit: false);

        Assert.Null(origin);
    }

    [Fact]
    public async Task A_hand_off_already_reported_is_not_carried_to_a_new_pass()
    {
        var row = await RowAsync(ProcessingFileStatuses.ProcessingFailed);
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", _library, Relative);
            return 0;
        });
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET state = 'completed' WHERE handoff_id = 'h1'");
        await QueueEarlierPassAsync(Payload(_library, """{"source_key":"deluno","handoff_id":"h1"}"""));

        Assert.Null(await _fixture.Db(uow => HandoffOriginCarry.FindAsync(uow, row.LibraryId, row.RelativePath), commit: false));
    }
}
