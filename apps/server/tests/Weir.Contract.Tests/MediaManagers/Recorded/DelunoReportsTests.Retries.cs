using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The reports Weir sends Deluno that Deluno answered with a refusal or a 409, replayed from the rc.30.32 recordings: the 503 Weir keeps owed
/// and the 401 and 403 it does not (when it sends the 503 again is held on a fake clock by <c>RecordedReportRetryTests</c>, which a running
/// server cannot have), and the two sends of one release, the first reported superseded and the second finished.
/// The recorded ids are Deluno's; this test makes them its own (<see cref="Translation.Id"/>), as it does for a recorded request.
/// </summary>
public sealed partial class DelunoReportsTests
{
    /// <summary>
    /// How long the report of a replaced send is waited for: it is owed and goes with the next run of the media manager heartbeat, which is a
    /// minute apart (<c>ManagerHeartbeatTask.Interval</c>), so the wait ends at the first run, not after a guessed delay.
    /// </summary>
    private static readonly TimeSpan NextHeartbeat = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_report_Deluno_answered_503_is_kept_owed_with_a_note_that_Weir_is_waiting_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var recorded = DelunoSide.Report("events-completed-answered-503-not-repeated");
        world.Deluno.Route("POST", ReportsTo, DelunoSide.AnswerOf(recorded.Exchanges[0].Reply));
        var hand = DelunoSide.HandoffFor(recorded.Exchanges[0]);
        var id = world.Translation.Id(hand.HandoffId());
        var path = world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, id);
        EmittedReport.AssertMatches(recorded, 0, report.Body, world.Translation);
        // Weir writes that it is waiting in the file's own words in the same step that keeps the report owed.
        await Poll.UntilAsync(
            async () => ((string?)(await world.Scenario.FileRowAsync(world.Movies, Path.GetFileName(path)))?["status_reason"])?.Contains("Weir will tell it this file is ready when it answers", StringComparison.Ordinal) == true ? "waiting" : null,
            "Weir to say it is waiting to tell Deluno");
        Assert.Single(world.Deluno.RequestsTo("POST", ReportsTo));
        Assert.Empty(await world.Scenario.ActivityAsync("processing.handoff_reported"));
        Assert.Equal([id], await OwedAsync(world, [id]));
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
            NextHeartbeat);
        EmittedReport.AssertMatches(superseded, 0, (JsonObject)replaced.Json!, world.Translation);
        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(firstId))["state"]!);
    }
}
