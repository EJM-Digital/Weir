using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// A Sonarr that is busy with another scan when Weir asks it to scan a file, on a fake clock and the recorded replies
/// (<see cref="BusyScanRecording"/>). The pass that asked finishes at once; the next ask is a job held back with <c>not_before</c>, so no
/// worker waits for the app. The first ask here runs as the pass runs it (<see cref="DownloadedScanNotifier.NotifyLibraryAsync"/>), every
/// later one as the job queue runs it (<see cref="DownloadedScanRetryHandler"/>). A 429 with a <c>Retry-After</c> is a standard HTTP answer
/// no recording holds, so that one test answers it itself.
/// </summary>
public sealed class DownloadedScanRetryTests : IDisposable
{
    private const string Relative = "Breaking Bad/Season 1/Breaking.Bad.S01E03.720p.WEB.h264-RECORDER.mkv";
    private const string AskingAgain = "% is busy with another scan, so Weir will ask it to scan for % again shortly";
    private const string Asked = "Asked % to scan for %";
    private const string CouldNotAsk = "Weir could not ask % to scan for %";

    private readonly MediaManagerFixture _fixture = new();
    private ArrReplay? _replay;
    private DownloadedScanNotifier? _notifier;
    private long _libraryId;
    private long _connectionId;

    public void Dispose()
    {
        _replay?.Dispose();
        _fixture.Dispose();
    }

    private DateTimeOffset Now => _fixture.Store.Clock.GetUtcNow();

    private async Task<DownloadedScanRetryHandler> StartAsync(IManagerHttpHandlerFactory handlers)
    {
        _libraryId = await _fixture.LibraryAsync(MediaManagerKinds.Tv, _fixture.Store.Home.Join("tv"));
        _connectionId = await _fixture.AddConnectionAsync(BusyScanRecording.App, "http://localhost:8989", "key");
        await _fixture.Store.Execute($"UPDATE media_manager_connections SET downloaded_scan_enabled = 1 WHERE id = {_connectionId}");
        await _fixture.Db(async uow => { await _fixture.Libraries.SetManagerLinksAsync(uow, _libraryId, [_connectionId]); return 0; });
        _notifier = new DownloadedScanNotifier(
            _fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, handlers, NullLogger<DownloadedScanNotifier>.Instance, _fixture.Jobs, _fixture.Store.Clock);
        return new DownloadedScanRetryHandler(_fixture.Jobs, _notifier, NullLogger<DownloadedScanRetryHandler>.Instance);
    }

    private Task<DownloadedScanRetryHandler> StartRecordedAsync(params ArrExchange[] scanAnswers)
    {
        _replay = new ArrReplay(BusyScanRecording.App, BusyScanRecording.Answering(scanAnswers));
        return StartAsync(_replay);
    }

    /// <summary>The pass asks for the scan, as a live pass that wrote the recorded file does.</summary>
    private Task<int> AskAsPassAsync() =>
        _fixture.Db(async uow =>
        {
            await _notifier!.NotifyLibraryAsync(uow, _libraryId, MediaManagerKinds.Tv, BusyScanRecording.OutputPath, Relative, downloadId: null);
            return 0;
        });

    private async Task<List<ProcessingJob>> QueuedAsync() =>
        [.. (await _fixture.Jobs.ListAsync()).Where(job => job.JobKind == DownloadedScanRetryHandler.Kind && job.Status == ProcessingJobStatus.Pending)];

    /// <summary>Takes what a worker of the upkeep lane would take now, runs it through the handler and completes it; false when nothing is due.</summary>
    private async Task<bool> RunDueJobAsync(DownloadedScanRetryHandler handler)
    {
        var claimed = await _fixture.Jobs.ClaimNextAsync(
            "test", Now.AddMinutes(5), lane: WorkLane.Upkeep, kinds: new ClaimableKinds([DownloadedScanRetryHandler.Kind], IncludeRefusedKinds: false));
        if (claimed is null)
        {
            return false;
        }

        await handler.HandleAsync(new JobWorkContext(claimed.Id, claimed.JobKind, claimed.PayloadJson, "test"), CancellationToken.None);
        await _fixture.Jobs.CompleteClaimedAsync(claimed.Id, "test");
        return true;
    }

    private List<ArrRequest> ScansAsked() =>
        [.. _replay!.Sent.Where(sent => sent is { Method: "POST", Path: DownloadedScanRules.CommandPath })];

    private Task<long> ActivityAsync(string titlePattern) =>
        _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE title LIKE '{titlePattern}'");

    [Fact]
    public async Task A_busy_app_does_not_hold_the_pass_the_next_ask_is_queued_for_fifteen_seconds_later()
    {
        await StartRecordedAsync(BusyScanRecording.Busy(), BusyScanRecording.Accepted);

        await AskAsPassAsync();

        Assert.Single(ScansAsked());
        var queued = Assert.Single(await QueuedAsync());
        Assert.Equal(Now + TimeSpan.FromSeconds(15), queued.NotBefore);
        Assert.Equal(1, await ActivityAsync(AskingAgain));
        Assert.Equal(0, await ActivityAsync(Asked));
        Assert.Equal(0, await ActivityAsync(CouldNotAsk));
        Assert.DoesNotContain(_replay!.Sent, sent => sent.Path.StartsWith(DownloadedScanRules.CommandPath + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_queued_ask_is_not_taken_before_its_time_and_the_second_ask_is_accepted()
    {
        var handler = await StartRecordedAsync(BusyScanRecording.Busy(), BusyScanRecording.Accepted);
        await AskAsPassAsync();

        _fixture.Store.Clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(await RunDueJobAsync(handler));
        Assert.Single(ScansAsked());

        _fixture.Store.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await RunDueJobAsync(handler));

        var asked = ScansAsked();
        Assert.Equal(2, asked.Count);
        Assert.True(JsonNode.DeepEquals(asked[0].Body, asked[1].Body));
        Assert.True(JsonNode.DeepEquals(BusyScanRecording.Accepted.Request.Body, asked[1].Body));
        Assert.Empty(await QueuedAsync());
        Assert.Equal(1, await ActivityAsync(Asked));
        Assert.Equal(0, await ActivityAsync(CouldNotAsk));
    }

    [Fact]
    public async Task An_app_that_stays_busy_is_asked_after_each_wait_and_then_Weir_stops_with_a_plain_sentence()
    {
        var handler = await StartRecordedAsync(BusyScanRecording.Busy());
        await AskAsPassAsync();

        foreach (var wait in DownloadedScanRetrySchedule.Waits)
        {
            var queued = Assert.Single(await QueuedAsync());
            Assert.Equal(Now + wait, queued.NotBefore);
            _fixture.Store.Clock.Advance(wait);
            Assert.True(await RunDueJobAsync(handler));
        }

        Assert.Empty(await QueuedAsync());
        Assert.Equal(1 + DownloadedScanRetrySchedule.MostAsksAgain, ScansAsked().Count);
        Assert.Equal(DownloadedScanRetrySchedule.MostAsksAgain, await ActivityAsync(AskingAgain));
        Assert.Equal(0, await ActivityAsync(Asked));
        Assert.Equal(1, await ActivityAsync(CouldNotAsk));
        var detail = (string?)await _fixture.Db(uow => uow.ScalarAsync("SELECT detail FROM activity_events WHERE title LIKE 'Weir could not ask%'"), commit: false);
        Assert.Contains("was still busy with another scan after Weir asked 6 times, so Weir has stopped asking.", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_switched_off_while_the_ask_waited_is_not_asked_again()
    {
        var handler = await StartRecordedAsync(BusyScanRecording.Busy(), BusyScanRecording.Accepted);
        await AskAsPassAsync();
        await _fixture.Store.Execute($"UPDATE media_manager_connections SET enabled = 0 WHERE id = {_connectionId}");

        _fixture.Store.Clock.Advance(TimeSpan.FromSeconds(15));
        await RunDueJobAsync(handler);

        Assert.Single(ScansAsked());
        Assert.Equal(0, await ActivityAsync(Asked));
    }

    [Fact]
    public async Task An_app_that_says_too_many_requests_with_a_Retry_After_is_asked_again_after_that_long_when_it_is_longer_than_the_wait()
    {
        var http = new FakeManagerHttp();
        http.Json(HttpMethod.Get, "/api/v3/remotepathmapping", "[]")
            .Route(HttpMethod.Post, DownloadedScanRules.CommandPath, _ => FakeManagerHttp.Response(HttpStatusCode.TooManyRequests, null, ("Retry-After", "45")));
        await StartAsync(http);

        await AskAsPassAsync();

        var queued = Assert.Single(await QueuedAsync());
        Assert.Equal(Now + TimeSpan.FromSeconds(45), queued.NotBefore);
        Assert.Equal(1, await ActivityAsync(AskingAgain));
    }
}
