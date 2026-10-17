using System.Text.Json.Nodes;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// A scan Weir asks for while the app is busy with another. The app keeps one Downloaded Scan at a time; asked for a second it can
/// answer with the scan it already has (Sonarr 4.0.20 did once, in a recording run: the answer carried the other scan's id and path, and
/// the file Weir asked about was never scanned). No run produces that answer on demand, so these tests take the recorded answer to the
/// scan and change the two members that make it another scan's: its path, and its id, which becomes the id of the command the recording
/// <c>command-status-finished</c> shows finished.
/// </summary>
public sealed class ArrDownloadedScanBusyRepliesTests
{
    private const string Sonarr = "sonarr";

    private const string BusyWith = "/downloads/Another.Release/Another.Release.mkv";

    private static ArrRecording NextFile => ArrRecording.Load(Sonarr, "downloaded-scan-next-file");

    private static ArrExchange Post(ArrRecording recording) => recording.To("POST", DownloadedScanRules.CommandPath);

    /// <summary>The recorded answer to the scan, made another scan's.</summary>
    private static ArrExchange AnotherScansAnswer()
    {
        var status = ArrRecording.Load(Sonarr, "command-status-finished").Only;
        var post = Post(NextFile);
        var body = post.Reply.Body!.DeepClone().AsObject();
        body["id"] = long.Parse(status.Request.Path[(status.Request.Path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        body["body"]!["path"] = BusyWith;
        return post with { Reply = post.Reply with { Body = body } };
    }

    private static Task<ArrDownloadedScanResult> RunAsync(ArrReplay replay) =>
        new ArrDownloadedScan(replay, new ElapsedAtOnce()).RequestAsync(replay.Connection(), MediaManagerKinds.Tv, (string)Post(NextFile).Request.Body!["path"]!, downloadId: null);

    [Fact]
    public async Task An_answer_that_is_another_scans_is_waited_out_and_the_scan_is_asked_for_again()
    {
        var recording = NextFile;
        var finished = ArrRecording.Load(Sonarr, "command-status-finished");
        var accepted = Post(recording);
        using var replay = new ArrReplay(Sonarr, recording.WithExchanges([recording.Exchanges[0], AnotherScansAnswer(), accepted]), finished);

        var result = await RunAsync(replay);

        Assert.True(result.Accepted, result.FailureReason);
        var asked = replay.Sent.Where(sent => sent is { Method: "POST", Path: DownloadedScanRules.CommandPath }).ToList();
        Assert.Equal(2, asked.Count);
        Assert.True(JsonNode.DeepEquals(asked[0].Body, asked[1].Body));
        Assert.True(JsonNode.DeepEquals(accepted.Request.Body, asked[0].Body));
        Assert.Contains(replay.Sent, sent => sent.Method == "GET" && sent.Path == finished.Only.Request.Path);
    }

    [Fact]
    public async Task A_manager_still_busy_after_every_attempt_is_not_called_accepted_and_the_reason_is_in_plain_words()
    {
        var recording = NextFile;
        var finished = ArrRecording.Load(Sonarr, "command-status-finished");
        using var replay = new ArrReplay(Sonarr, recording.WithExchanges([recording.Exchanges[0], AnotherScansAnswer()]), finished);

        var result = await RunAsync(replay);

        Assert.False(result.Accepted);
        Assert.Equal(
            $"{replay.Connection().Label} was still busy with another scan and did not take this one. Weir asked {DownloadedScanRules.MaxAttempts} times.",
            result.FailureReason);
        Assert.Equal(DownloadedScanRules.MaxAttempts, replay.Sent.Count(sent => sent is { Method: "POST", Path: DownloadedScanRules.CommandPath }));
    }

    [Fact]
    public async Task A_scan_that_is_not_in_the_way_is_asked_for_once_and_never_waited_for()
    {
        var recording = NextFile;
        using var replay = new ArrReplay(Sonarr, recording);

        var result = await RunAsync(replay);

        Assert.True(result.Accepted);
        Assert.Single(replay.Sent, sent => sent.Method == "POST");
        replay.AssertSentAsRecorded(recording);
    }
}
