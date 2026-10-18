using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>The steps the tests of Weir's reports to Deluno share: handing a release over, and reading the report a fake Deluno received for it.</summary>
internal static class DelunoReportSteps
{
    /// <summary>A report as the fake Deluno received it, and its body.</summary>
    public sealed record Sent(RecordedRequest Request, JsonObject Body);

    public static async Task HandOffAsync(RecordedWorld world, RecordedCall hand)
    {
        var accepted = await world.Replay.SendCallAsync(hand);
        Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());
    }

    /// <summary>The report Weir sent for the hand-off, once it has, for as long as a worker has the job that makes it (see <see cref="JobWait"/>).</summary>
    public static async Task<Sent> ReportAsync(RecordedWorld world, string handoffId)
    {
        var request = await new JobWait().UntilAsync(
            world.Scenario.JobStatusesAsync,
            () => Task.FromResult(world.Deluno.RequestsTo("POST", Scenario.EventsPath).FirstOrDefault(sent => ((JsonObject)sent.Json!)["handoffId"]!.ToString() == handoffId)),
            $"Weir's report of {handoffId}");
        return new Sent(request, (JsonObject)request.Json!);
    }
}
