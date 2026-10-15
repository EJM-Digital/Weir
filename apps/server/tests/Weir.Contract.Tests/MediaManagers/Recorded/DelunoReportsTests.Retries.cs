using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The reports Weir sends Deluno that Deluno answered with a refusal or a 409, replayed from the rc.30.32 recordings: the 503 Weir sends
/// again and the 401 and 403 it does not, and the two sends of one release, the first reported superseded and the second finished.
/// The recorded ids are Deluno's; this test makes them its own (<see cref="Translation.Id"/>), as it does for a recorded request.
/// </summary>
public sealed partial class DelunoReportsTests
{
    /// <summary>The longest a report owed to the heartbeat is waited for: the heartbeat runs every minute and the first wait is one.</summary>
    private static readonly TimeSpan HeartbeatCeiling = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task A_report_Deluno_answered_503_is_kept_and_sent_again_by_the_heartbeat_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var recorded = DelunoSide.Report("events-completed-answered-503-not-repeated");
        var accepted = DelunoSide.Request("events-completed").Exchanges[0].Reply;
        var answered = 0;
        world.Deluno.Route("POST", ReportsTo, _ => DelunoSide.AnswerOf(Interlocked.Increment(ref answered) == 1 ? recorded.Exchanges[0].Reply : accepted));
        var hand = DelunoSide.HandoffFor(recorded.Exchanges[0]);
        var id = world.Translation.Id(hand.HandoffId());
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var first = await ReportAsync(world, id);
        EmittedReport.AssertMatches(recorded, 0, first.Body, world.Translation);
        var sent = await Poll.UntilAsync(
            () => Task.FromResult(world.Deluno.RequestsTo("POST", ReportsTo) is { Count: >= 2 } requests ? requests : null),
            "Weir to send the report Deluno answered 503 again",
            HeartbeatCeiling);
        Assert.Equal(2, sent.Count);
        Assert.Equal(first.Body.ToJsonString(), ((JsonObject)sent[1].Json!).ToJsonString());
        var file = DelunoSide.FileNameOf((string)first.Body["outputPath"]!);
        var entry = Assert.Single(await RecordedReportsAsync(world, 1));
        Assert.Equal($"Told {Deluno(world)} that {file} is ready to import", (string)entry["title"]!);
        Assert.Empty(await OwedAsync(world, [id]));
        Assert.Equal(2, world.Deluno.RequestsTo("POST", ReportsTo).Count);
    }

    [Theory]
    [InlineData("events-answered-401-not-repeated")]
    [InlineData("events-answered-403-not-repeated")]
    public async Task A_report_Deluno_answered_401_or_403_is_not_owed_any_more_and_the_person_is_told_as_Deluno_recorded(string state)
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var recorded = DelunoSide.Report(state);
        world.Deluno.Route("POST", ReportsTo, DelunoSide.AnswerOf(recorded.Exchanges[0].Reply));
        var hand = DelunoSide.HandoffFor(recorded.Exchanges[0]);
        var id = world.Translation.Id(hand.HandoffId());
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, id);
        EmittedReport.AssertMatches(recorded, 0, report.Body, world.Translation);
        var file = DelunoSide.FileNameOf((string)report.Body["outputPath"]!);
        var entry = Assert.Single(await RecordedReportsAsync(world, 1));
        Assert.Equal($"Weir could not tell {Deluno(world)} about {file}", (string)entry["title"]!);
        Assert.Equal("failed", (string)JsonNode.Parse((string)entry["detail"]!)!["result"]!);
        Assert.Equal("completed", (string)(await world.Scenario.HandoffStatusAsync(id))["state"]!);
        Assert.Single(world.Deluno.RequestsTo("POST", ReportsTo));
        Assert.Empty(await OwedAsync(world, [id]));
    }

    [Fact]
    public async Task A_release_sent_twice_is_reported_superseded_for_the_first_send_and_finished_for_the_second_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var superseded = DelunoSide.Report("events-superseded");
        var takeover = DelunoSide.Report("events-completed-after-takeover");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, superseded, takeover);
        var release = world.HoldEveryPass();
        var second = DelunoSide.HandoffFor(takeover.Exchanges[0]);
        var first = DelunoSide.HandoffFor(superseded.Exchanges[0], DelunoSide.FileNameOf((string)takeover.Exchanges[0].Request.Body!["outputPath"]!));
        var firstId = world.Translation.Id(first.HandoffId());
        world.PutFilm(first.SourcePath(), needsWork: true);
        await HandOffAsync(world, first);
        await world.WaitUntilWorkingAsync(firstId);

        await HandOffAsync(world, second);
        await File.WriteAllTextAsync(release, string.Empty);

        EmittedReport.AssertMatches(takeover, 0, (await ReportAsync(world, world.Translation.Id(second.HandoffId()))).Body, world.Translation);
        var replaced = await Poll.UntilAsync(
            () => Task.FromResult(world.Deluno.RequestsTo("POST", ReportsTo).FirstOrDefault(sent => ((JsonObject)sent.Json!)["handoffId"]!.ToString() == firstId)),
            "Weir to tell Deluno the first send was replaced",
            HeartbeatCeiling);
        EmittedReport.AssertMatches(superseded, 0, (JsonObject)replaced.Json!, world.Translation);
        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(firstId))["state"]!);
    }
}
