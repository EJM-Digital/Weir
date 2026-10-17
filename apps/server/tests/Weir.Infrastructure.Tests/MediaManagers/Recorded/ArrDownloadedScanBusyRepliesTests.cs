using System.Text.Json.Nodes;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// A scan Weir asks for while the app is busy with another (<see cref="BusyScanRecording"/>). Weir asks once and reports that the app is
/// busy; it does not wait for the other scan, poll the app or ask again, since a worker must never sit waiting on another app. Asking again
/// later is the caller's to schedule (<c>DownloadedScanRetryTests</c>).
/// </summary>
public sealed class ArrDownloadedScanBusyRepliesTests
{
    private static Task<ArrDownloadedScanResult> RunAsync(ArrReplay replay) =>
        new ArrDownloadedScan(replay).RequestAsync(replay.Connection(), MediaManagerKinds.Tv, BusyScanRecording.OutputPath, downloadId: null);

    [Fact]
    public async Task An_answer_that_is_another_scans_is_reported_busy_after_one_ask_with_nothing_waited_for()
    {
        using var replay = new ArrReplay(BusyScanRecording.App, BusyScanRecording.Answering(BusyScanRecording.Busy()), ArrRecording.Load(BusyScanRecording.App, "command-status-finished"));

        var result = await RunAsync(replay);

        Assert.True(result.Busy);
        Assert.False(result.Accepted);
        Assert.Null(result.RetryAfter);
        var asked = Assert.Single(replay.Sent, sent => sent is { Method: "POST", Path: DownloadedScanRules.CommandPath });
        Assert.True(JsonNode.DeepEquals(BusyScanRecording.Accepted.Request.Body, asked.Body));
        Assert.DoesNotContain(replay.Sent, sent => sent.Path.StartsWith(DownloadedScanRules.CommandPath + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_that_is_not_in_the_way_is_asked_for_once_and_accepted()
    {
        var recording = BusyScanRecording.NextFile;
        using var replay = new ArrReplay(BusyScanRecording.App, recording);

        var result = await RunAsync(replay);

        Assert.True(result.Accepted);
        Assert.False(result.Busy);
        Assert.Single(replay.Sent, sent => sent.Method == "POST");
        replay.AssertSentAsRecorded(recording);
    }
}
