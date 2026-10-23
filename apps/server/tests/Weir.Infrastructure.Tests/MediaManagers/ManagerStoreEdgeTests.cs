using Weir.Core.Jobs;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Requests, hand-offs and folders that are not the ordinary kind.</summary>
public sealed class ManagerStoreEdgeTests : IDisposable
{
    private const string Relative = "Film (2020)/Film (2020).mkv";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_job_to_ask_a_manager_to_scan_again_that_carries_no_request_is_dropped_with_a_warning()
    {
        var notifier = new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http);
        var logger = new CapturingLogger<DownloadedScanRetryHandler>();
        var handler = new DownloadedScanRetryHandler(_fixture.Jobs, notifier, logger);

        await handler.HandleAsync(new JobWorkContext(7, handler.JobKind, "{}", "test"), CancellationToken.None);

        Assert.Empty(_fixture.Http.Requests);
        Assert.True(logger.Logged(Microsoft.Extensions.Logging.LogLevel.Warning, "Job 7 to ask a media manager to scan again carries no usable request"));
    }

    [Fact]
    public async Task A_redownload_without_a_matched_title_id_is_refused_by_name()
    {
        var connection = new ManagerConnection("radarr", "Main", "http://manager.local", "k", 1);

        var refused = await Assert.ThrowsAsync<MediaManagerHttpException>(() =>
            new ArrManagerRedownload(_fixture.Http).RequestRedownloadAsync(connection, MediaManagerKinds.Movie, "not-a-number", "/movies/film.mkv"));

        Assert.Contains("needs a matched title id to redownload /movies/film.mkv", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_movie_the_manager_answers_with_something_that_is_not_a_movie_is_refused()
    {
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/movie/7", "[]");
        var connection = new ManagerConnection("radarr", "Main", "http://manager.local", "k", 1);

        var refused = await Assert.ThrowsAsync<MediaManagerHttpException>(() =>
            new ArrManagerRedownload(_fixture.Http).RequestRedownloadAsync(connection, MediaManagerKinds.Movie, "7", "/movies/film.mkv"));

        Assert.Contains("did not return a movie for id 7", refused.Message, StringComparison.Ordinal);
        Assert.Single(_fixture.Http.Requests);
    }

    [Fact]
    public void A_folder_that_is_not_there_cannot_be_written_to()
    {
        using var temp = new TempDirectory();

        Assert.False(new FilesystemFolderProbe().CanWrite(temp.Join("missing")));
    }

    [Fact]
    public async Task A_hand_off_that_ended_cannot_be_ended_again_because_a_queued_pass_was_cancelled()
    {
        var row = await RecordAsync(finished: true);

        var ended = await _fixture.Db(uow => _fixture.Ledger.SettleAfterJobCancelledAsync(uow, row, _fixture.Store.Clock.GetUtcNow(), "Cancelled."));

        Assert.False(ended);
    }

    [Fact]
    public async Task A_cancelled_file_of_a_hand_off_with_no_listed_files_is_not_tracked()
    {
        var row = await RecordAsync(finished: false);

        var finish = await _fixture.Db(uow => _fixture.Targets.MarkCancelledAsync(uow, row, Relative));

        Assert.Equal(HandoffTargetProgress.Untracked, finish.Progress);
    }

    private async Task<HandoffLedgerRow> RecordAsync(bool finished)
    {
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId: null, Relative);
            if (finished)
            {
                await _fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "h1", HandoffLedgerRules.Completed, null, "Done.", null);
            }

            return 0;
        });
        return (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h1"), commit: false))!;
    }
}
