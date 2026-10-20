using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The folder a report names is the one Deluno sees (<c>processorOutputPath</c>), which Weir reads from Deluno's manifest as the file finishes.
/// A manifest that fails once, as a Deluno does for a request it drops, must not make the report name Weir's own folder.
/// </summary>
public sealed partial class DelunoReportsTests
{
    private const string ManifestPath = "/api/integrations/external/manifest";

    [Fact]
    public async Task A_report_names_the_folder_Deluno_sees_when_Deluno_fails_the_first_read_of_its_libraries_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var hand = HandoffFor(recorded, 0);
        var failedReads = 0;
        world.Deluno.Route("GET", ManifestPath, _ =>
            Interlocked.Increment(ref failedReads) == 1
                ? new Reply(503)
                : Reply.Ok(new JsonObject
                {
                    ["name"] = "Deluno",
                    ["libraries"] = new JsonArray([.. world.Deluno.Libraries.Snapshot().Select(library => (JsonNode)library.DeepClone())]),
                    ["capabilities"] = new JsonArray(),
                }));
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
        Assert.True(failedReads > 1, "the first read of the manifest was never refused, so the report was not placed from a second one");
    }
}
