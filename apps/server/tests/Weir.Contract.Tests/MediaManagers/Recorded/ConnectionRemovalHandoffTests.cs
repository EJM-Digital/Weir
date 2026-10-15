using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Removing Deluno while Weir is still working a hand-off Deluno made, or still owes Deluno its report. The hand-off is the one Deluno
/// recorded sending (<c>handoff-single-file</c>, rc.30.30) and the report is answered as Deluno recorded answering it, so what Weir
/// refuses, and when it stops refusing, is judged against Deluno's own traffic. The download Deluno handed over is never touched.
/// </summary>
[ContractArea("media_managers")]
public sealed class ConnectionRemovalHandoffTests
{
    private static async Task<(int Id, string Label)> ConnectionAsync(RecordedWorld world)
    {
        var listed = await world.Scenario.Admin.GetAsync(ManagerConnections.Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        var connection = Assert.Single(listed.Elements)!;
        return (JsonFields.Id(connection), (string)connection["name"]!);
    }

    private static Task<WeirResponse> UnlinkAsync(RecordedWorld world, int connectionId) =>
        world.Scenario.Admin.PostWithCsrfAsync($"{ManagerConnections.Route}/{connectionId}/unlink-workflows");

    private static async Task AssertWorkflowsStillLinkedAsync(RecordedWorld world, int connectionId)
    {
        foreach (var library in await ProcessingLibraries.ListAsync(world.Scenario.Admin))
        {
            Assert.Equal([connectionId], library!["manager_connection_ids"]!.AsArray().Select(id => (int)id!));
            Assert.True((bool)library["enabled"]!, $"{library["name"]} must keep running while it is linked");
        }
    }

    private static string[] Names(JsonNode? workflows) => [.. workflows!.AsArray().Select(item => (string)item!["name"]!)];

    [Fact]
    public async Task A_hand_off_in_Weir_holds_Deluno_and_its_workflows_until_it_is_done_and_the_download_is_never_touched()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        world.Deluno.AnswerReportsAsRecorded(DelunoSide.Request("events-completed"));
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Only.Request;
        var release = world.HoldEveryPass();
        var source = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));
        await world.Replay.SendAsync(recorded);
        await world.WaitUntilWorkingAsync(hand.HandoffId());
        var (connectionId, label) = await ConnectionAsync(world);

        var refused = await ManagerConnections.DeleteAsync(world.Scenario.Admin, connectionId);
        var unlinkRefused = await UnlinkAsync(world, connectionId);

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("connection_in_use", (string)refused.Fields["code"]!);
        Assert.Equal(["Movies", "TV Shows"], Names(refused.Fields["workflows"]));
        Assert.Equal((1, 0), ((int)refused.Fields["handoffs_in_progress"]!, refused.Fields["reports"]!.AsArray().Count));
        Assert.Equal(
            $"Movies and TV Shows still come from {label}. Weir is still working on 1 hand-off from it. Nothing was removed. " +
            "Before removing it, unlink or remove those workflows and let the hand-off finish or cancel it.",
            (string)refused.Fields["detail"]!);
        Assert.Equal(HttpStatusCode.Conflict, unlinkRefused.Status);
        Assert.Equal("handoffs_in_progress", (string)unlinkRefused.Fields["code"]!);
        await AssertWorkflowsStillLinkedAsync(world, connectionId);
        source.AssertStill();

        await File.WriteAllTextAsync(release, string.Empty);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        await Poll.UntilAsync(() => Task.FromResult(world.Deluno.RequestsTo("POST", Scenario.EventsPath).Count == 1 ? "reported" : null), "Weir's report to Deluno");

        var stillFeeds = await ManagerConnections.DeleteAsync(world.Scenario.Admin, connectionId);
        Assert.Equal(HttpStatusCode.Conflict, stillFeeds.Status);
        Assert.Equal((0, 0), ((int)stillFeeds.Fields["handoffs_in_progress"]!, stillFeeds.Fields["reports"]!.AsArray().Count));
        var unlinked = await UnlinkAsync(world, connectionId);
        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());
        Assert.Equal(["Movies", "TV Shows"], Names(unlinked.Fields["workflows"]));
        foreach (var library in await ProcessingLibraries.ListAsync(world.Scenario.Admin))
        {
            Assert.Empty(library!["manager_connection_ids"]!.AsArray());
            Assert.False((bool)library["enabled"]!);
        }

        var removed = await ManagerConnections.DeleteAsync(world.Scenario.Admin, connectionId);

        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        source.AssertStill();
    }

    [Fact]
    public async Task A_report_Weir_could_not_deliver_holds_Deluno_even_with_its_workflows_unlinked()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Only.Request;
        var release = world.HoldEveryPass();
        var source = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));
        await world.Replay.SendAsync(recorded);
        await world.WaitUntilWorkingAsync(hand.HandoffId());
        var (connectionId, label) = await ConnectionAsync(world);

        // Deluno stops answering before the pass ends, so Weir keeps its report to send when it answers again.
        world.Deluno.Dispose();
        await File.WriteAllTextAsync(release, string.Empty);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        await Poll.UntilAsync(async () => await OwesReportAsync(world, hand.HandoffId()) ? "owed" : null, "Weir to keep its report for Deluno");
        var unlinked = await UnlinkAsync(world, connectionId);
        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());

        var refused = await ManagerConnections.DeleteAsync(world.Scenario.Admin, connectionId);

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("connection_in_use", (string)refused.Fields["code"]!);
        Assert.Empty(refused.Fields["workflows"]!.AsArray());
        var fileName = Path.GetFileName(world.Translation.Apply(hand.SourcePath()));
        Assert.Equal(0, (int)refused.Fields["handoffs_in_progress"]!);
        Assert.Equal([(hand.HandoffId(), fileName)], refused.Fields["reports"]!.AsArray().Select(report => ((string)report!["handoff_id"]!, (string)report["name"]!)));
        Assert.Equal(
            $"Weir still owes {label} a report on a finished hand-off. Nothing was removed. Before removing it, let Weir deliver the report it owes or give up on it.",
            (string)refused.Fields["detail"]!);
        Assert.Single((await world.Scenario.Admin.GetAsync(ManagerConnections.Route)).Elements);

        // Deluno never comes back: the person gives up on the report, which is recorded, and the connection can then go.
        var gaveUp = await world.Scenario.Admin.PostWithCsrfAsync($"{ManagerConnections.Route}/{connectionId}/give-up-reports");
        Assert.True(gaveUp.Status == HttpStatusCode.OK, gaveUp.ToString());
        Assert.Equal([hand.HandoffId()], gaveUp.Fields["reports"]!.AsArray().Select(report => (string)report!["handoff_id"]!));
        Assert.Equal(
            [$"Weir gave up on a report it owed {label}"],
            await world.ActivityTitlesAsync("processing.handoff_reports_dropped"));
        Assert.False(await OwesReportAsync(world, hand.HandoffId()));
        var removed = await ManagerConnections.DeleteAsync(world.Scenario.Admin, connectionId);
        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        source.AssertStill();
    }

    /// <summary>Whether Weir holds a report for the hand-off, read from its database while it is stopped.</summary>
    private static async Task<bool> OwesReportAsync(RecordedWorld world, string handoffId) =>
        (await world.Scenario.RowsAfterStopAsync("SELECT handoff_id FROM media_manager_handoffs WHERE source_key = 'deluno' AND pending_report_json IS NOT NULL"))
        .Any(row => (string)row["handoff_id"]! == handoffId);
}
