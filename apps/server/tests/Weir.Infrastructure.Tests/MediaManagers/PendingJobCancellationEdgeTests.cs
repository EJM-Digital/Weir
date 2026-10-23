using Weir.Core.Jobs;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Cancelling a queued pass whose payload does not point at a file the lists know leaves every file as it was.</summary>
public sealed class PendingJobCancellationEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("{relative_media_path: 'a.mkv', library_id: 1,}")]
    [InlineData("[1]")]
    [InlineData("{\"relative_media_path\":\"a.mkv\"}")]
    [InlineData("{\"relative_media_path\":\"a.mkv\",\"library_id\":0}")]
    [InlineData("{\"relative_media_path\":\"\",\"library_id\":1}")]
    public async Task A_queued_pass_that_names_no_known_file_is_cancelled_and_no_file_changes(string payload)
    {
        await _fixture.Store.Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (41, 'Films', 'movie', '/in', 41)");
        await _fixture.Store.Execute("INSERT INTO files (library_id, relative_path, status) VALUES (41, 'a.mkv', 'unprocessed')");
        var job = await _fixture.Jobs.EnqueueOrGetAsync("cancel-me", "processing.file.remux_pass.v1", payload);

        var result = await _fixture.Db(uow => _fixture.Cancellation.CancelAsync(uow, job.Id));

        Assert.Equal(JobActionOutcome.Ok, result.Outcome);
        Assert.Null(result.EndedHandoff);
        Assert.Equal("cancelled", await _fixture.Store.ScalarText("SELECT status FROM jobs WHERE id = " + job.Id));
        Assert.Equal("unprocessed", await _fixture.Store.ScalarText("SELECT status FROM files WHERE library_id = 41"));
    }

    [Fact]
    public async Task A_queued_job_of_another_kind_is_cancelled_without_marking_its_file_cancelled()
    {
        await _fixture.Store.Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (41, 'Films', 'movie', '/in', 41)");
        await _fixture.Store.Execute("INSERT INTO files (library_id, relative_path, status) VALUES (41, 'a.mkv', 'unprocessed')");
        var job = await _fixture.Jobs.EnqueueOrGetAsync("cancel-pass-through", IntakeRules.PassThroughJobKind, "{\"relative_media_path\":\"a.mkv\",\"library_id\":41}");

        var result = await _fixture.Db(uow => _fixture.Cancellation.CancelAsync(uow, job.Id));

        Assert.Equal(JobActionOutcome.Ok, result.Outcome);
        Assert.Equal("unprocessed", await _fixture.Store.ScalarText("SELECT status FROM files WHERE library_id = 41"));
    }

    [Fact]
    public async Task A_queued_pass_for_a_hand_off_that_names_no_file_is_cancelled_and_the_hand_off_is_still_settled()
    {
        await _fixture.Store.Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (41, 'Films', 'movie', '/in', 41)");
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", 41, "a.mkv");
            return 0;
        });
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            "cancel-handoff", "processing.file.remux_pass.v1", "{\"origin\":{\"source_key\":\"deluno\",\"handoff_id\":\"h1\"}}");

        var result = await _fixture.Db(uow => _fixture.Cancellation.CancelAsync(uow, job.Id));

        Assert.Equal(JobActionOutcome.Ok, result.Outcome);
        Assert.Equal("cancelled", await _fixture.Store.ScalarText("SELECT status FROM jobs WHERE id = " + job.Id));
    }
}
