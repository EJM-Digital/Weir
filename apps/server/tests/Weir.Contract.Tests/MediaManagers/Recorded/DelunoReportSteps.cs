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

    /// <summary>
    /// The first look at a file that is gone is not final: Weir looks again after the vanished-file grace and a minute, and only then tells Deluno.
    /// The second look is made due now, as it would be once that time has passed; the file shows as gone a moment before the look is booked, so
    /// the server is stopped and asked again until it is there.
    /// </summary>
    public static async Task MakeSecondLookDueAsync(RecordedWorld world) =>
        await Poll.UntilAsync(
            async () =>
            {
                await using var database = await world.Scenario.Server.StopForDatabaseAsync();
                var due = SeedSql.Execute(
                    database.Connection,
                    "UPDATE jobs SET not_before = $past WHERE status = 'pending' AND dedupe_key LIKE '%:gone-wait:%'",
                    ("$past", Handoffs.StoredTime(DateTime.UtcNow.AddMinutes(-1))));
                return due > 0 ? "due" : null;
            },
            "Weir to book a second look at the gone file");
}
