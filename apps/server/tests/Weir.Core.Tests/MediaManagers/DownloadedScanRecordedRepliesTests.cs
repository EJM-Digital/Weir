using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="DownloadedScanRules"/> reading what a real Sonarr and a real Radarr answered to a scan request and to a question about a
/// scan (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>).
/// </summary>
public sealed class DownloadedScanRecordedRepliesTests
{
    public static TheoryData<string> Apps => ["sonarr", "radarr"];

    private static WireValue? Reply(ArrExchange exchange) => WireJsonParser.Parse(exchange.Reply.Text);

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_answer_to_the_scan_that_was_asked_for_is_that_scan(string app)
    {
        var exchange = ArrRecording.Load(app, "downloaded-scan").To("POST", DownloadedScanRules.CommandPath);
        var asked = (string)exchange.Request.Body!["path"]!;

        Assert.False(DownloadedScanRules.IsAnotherScan(Reply(exchange), asked, out var otherId));
        Assert.Null(otherId);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void An_answer_that_names_a_different_path_is_another_scan_and_its_id_is_kept(string app)
    {
        var exchange = ArrRecording.Load(app, "downloaded-scan").To("POST", DownloadedScanRules.CommandPath);
        var answered = (long)exchange.Reply.Body!["id"]!;

        Assert.True(DownloadedScanRules.IsAnotherScan(Reply(exchange), "/downloads/Some.Other.Release/Some.Other.Release.mkv", out var otherId));
        Assert.Equal(answered, otherId);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void Paths_that_differ_only_in_separator_or_a_trailing_slash_are_the_same_path(string app)
    {
        var exchange = ArrRecording.Load(app, "downloaded-scan").To("POST", DownloadedScanRules.CommandPath);
        var asked = (string)exchange.Request.Body!["path"]!;

        Assert.False(DownloadedScanRules.IsAnotherScan(Reply(exchange), asked.Replace('/', '\\') + "\\", out _));
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void An_answer_with_no_command_body_says_nothing_about_a_path_and_is_taken_as_asked(string app)
    {
        var exchange = ArrRecording.Load(app, "downloaded-scan").To("POST", DownloadedScanRules.CommandPath);
        var withoutBody = exchange.Reply.Body!.DeepClone().AsObject();
        withoutBody.Remove("body");

        Assert.False(DownloadedScanRules.IsAnotherScan(WireJsonParser.Parse(withoutBody.ToJsonString()), "/anywhere.mkv", out _));
        Assert.False(DownloadedScanRules.IsAnotherScan(null, "/anywhere.mkv", out _));
    }

    [Fact]
    public void A_command_the_app_reports_as_completed_is_finished_and_one_still_started_is_not()
    {
        var finished = ArrRecording.Load("sonarr", "command-status-finished").Only;
        var started = finished.Reply.Body!.DeepClone().AsObject();
        started["status"] = "started";

        Assert.Equal("completed", (string)finished.Reply.Body!["status"]!);
        Assert.True(DownloadedScanRules.IsFinished(Reply(finished)));
        Assert.False(DownloadedScanRules.IsFinished(WireJsonParser.Parse(started.ToJsonString())));
        Assert.False(DownloadedScanRules.IsFinished(null));
    }
}
