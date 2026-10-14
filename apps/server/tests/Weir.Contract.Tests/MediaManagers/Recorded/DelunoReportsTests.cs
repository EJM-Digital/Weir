using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What Weir tells Deluno, held to what the real Weir (rc.13) told the real Deluno (rc.30.30) for a hand-off that finished, one that failed,
/// one whose download was gone, and one Deluno had stopped waiting for: the same members in the same order, of the same types, with the
/// exact words <c>status</c>, <c>failureClass</c> (<c>source_gone</c>) and <c>disposition</c>. A fake Deluno records what Weir POSTs and
/// answers with the reply Deluno recorded for that report; Weir must read a 202 as delivered and a 409 as Deluno having stopped waiting.
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoReportsTests
{
    private const string ReportsTo = Scenario.EventsPath;

    [Fact]
    public async Task A_finished_single_file_release_folder_and_Usenet_folder_are_each_reported_as_Deluno_recorded_and_the_202_settles_them()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var handoffs = new[] { "handoff-single-file", "handoff-release-folder", "handoff-usenet-folder" }.Select(DelunoSide.Request).ToList();

        var ids = new List<string>();
        for (var index = 0; index < handoffs.Count; index++)
        {
            var hand = handoffs[index].Only.Request;
            PutFilms(world, hand);
            await world.Replay.SendAsync(handoffs[index]);

            var report = await ReportAsync(world, hand.HandoffId());

            EmittedReport.AssertMatches(recorded, index, report.Body);
            Assert.Equal(world.Deluno.ApiKey, report.Request.Header("X-Api-Key"));
            Assert.Equal("application/json", report.Request.Header("Content-Type")?.Split(';')[0]);
            ids.Add(hand.HandoffId());
        }

        Assert.Equal(
            recorded.Exchanges.Select(exchange => $"Told {Deluno(world)} that {FileNameIn((string)exchange.Request.Body!["outputPath"]!)} is ready to import").Order(),
            (await world.Scenario.ActivityAsync("processing.handoff_reported")).Select(entry => (string)entry["title"]!).Order());
        Assert.Empty(await OwedAsync(world, ids));
    }

    [Fact]
    public async Task A_file_under_the_workflows_minimum_size_is_reported_failed_and_held_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-failed-under-minimum");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("min_file_size_mb", 50));
        var hand = HandoffFor(recorded, 0);
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
    }

    [Fact]
    public async Task A_file_Weir_cannot_read_is_reported_failed_and_held_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-failed-unreadable");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var hand = HandoffFor(recorded, 0);
        var junk = new byte[256 * 1024];
        new Random(971).NextBytes(junk);
        var path = world.Translation.Apply(hand.SourcePath());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, junk);
        world.Scenario.FakeTools.SetFileRule(Path.GetFileName(path), new FileRule { ProbeError = "Invalid data found when processing input" });

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
        Assert.Equal(junk, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_download_that_is_gone_before_Weir_starts_is_reported_as_source_gone_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-failed-source-gone");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var hand = HandoffFor(recorded, 0);
        await world.Scenario.SetPauseAsync(paused: true);
        var path = world.PutFilm(hand.SourcePath(), needsWork: true);
        await HandOffAsync(world, hand);
        File.Delete(path);

        await world.Scenario.SetPauseAsync(paused: false);
        await Poll.UntilAsync(
            async () => (bool?)(await world.Scenario.FileRowAsync(world.Movies, Path.GetFileName(path)))?["source_gone"] is true ? "held as gone" : null,
            "Weir to hold the file as gone after its first look");
        // The first look is not final: Weir looks again after the vanished-file grace and a minute, and only then tells Deluno. The
        // second look is made due now, as it would be once that time has passed; the file shows as gone a moment before the look is
        // booked, so the server is stopped and asked again until it is there.
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

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
    }

    [Fact]
    public async Task A_report_of_a_release_Deluno_removed_is_answered_409_dismissed_and_Weir_stops_owing_it()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var tooLate = DelunoSide.Request("events-too-late");
        world.Deluno.AnswerReportsAsRecorded(tooLate);
        var sends = DelunoSide.Request("handoff-resend");
        var first = sends.Exchanges[0].Request;
        world.PutFilm(first.SourcePath(), needsWork: true);

        await world.Replay.SendAsync(sends, 0);

        var report = await ReportAsync(world, first.HandoffId());
        EmittedReport.AssertMatches(tooLate, 0, report.Body);

        // Deluno answered, so Weir does not say it could not reach it or that its address or key are wrong: it says Deluno had already
        // stopped waiting, and that no import was queued, and does not ask a person to check anything.
        var file = DelunoSide.ReleaseNameOf(tooLate.Exchanges[0]);
        var entry = Assert.Single(await world.Scenario.ActivityAsync("processing.handoff_reported"));
        Assert.Equal($"{Deluno(world)} had already stopped waiting for {file}", (string)entry["title"]!);
        var detail = JsonNode.Parse((string)entry["detail"]!)!.AsObject();
        Assert.Equal("skipped", (string)detail["result"]!);
        Assert.Equal($"{Deluno(world)} had already stopped waiting for {file}, so it did not queue an import.", (string)detail["user_message"]!);
        Assert.False(detail.ContainsKey("next_action"), "nothing here is for a person to check");
        Assert.Equal("completed", (string)(await world.Scenario.HandoffStatusAsync(first.HandoffId()))["state"]!);
        Assert.Single(world.Deluno.RequestsTo("POST", ReportsTo));
        Assert.Empty(await OwedAsync(world, [first.HandoffId()]));
    }

    [Fact]
    public async Task A_release_sent_again_after_it_was_cleaned_is_reported_finished_with_the_copy_made_before_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded, DelunoSide.Request("events-too-late"));
        var sends = DelunoSide.Request("handoff-resend");
        world.PutFilm(sends.Exchanges[0].Request.SourcePath(), needsWork: true);
        await world.Replay.SendAsync(sends, 0);
        await ReportAsync(world, sends.Exchanges[0].Request.HandoffId());
        await world.Replay.SendAsync(DelunoSide.Request("outcome-not-imported-removed"), 0);

        await world.Replay.SendAsync(sends, 1);

        var report = await ReportAsync(world, sends.Exchanges[1].Request.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
    }

    [Fact]
    public async Task A_release_sent_again_while_the_first_send_is_in_Weir_is_reported_finished_once_for_the_new_send_only()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var release = world.HoldEveryPass();
        var first = HandoffFor(DelunoSide.Request("events-too-late"), 1);
        var again = HandoffFor(recorded, 1);
        world.PutFilm(first.SourcePath(), needsWork: true);
        await HandOffAsync(world, first);
        await world.WaitUntilWorkingAsync(first.HandoffId());

        await HandOffAsync(world, again);
        await File.WriteAllTextAsync(release, string.Empty);

        var report = await ReportAsync(world, again.HandoffId());
        EmittedReport.AssertMatches(recorded, 1, report.Body);
        await world.Scenario.WaitForHandoffStateAsync(again.HandoffId(), "completed");
        // The rc.13 recordings have Weir also telling Deluno the replaced send was completed (answered 409, too late, events-too-late
        // exchange 1). Since #956 the replaced send is closed as cancelled and is never reported as finished.
        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(first.HandoffId()))["state"]!);
        await Scenario.NeverWithinAsync(
            () => Task.FromResult(world.Deluno.RequestsTo("POST", ReportsTo).Any(sent => ((JsonObject)sent.Json!)["handoffId"]!.ToString() == first.HandoffId() && ((JsonObject)sent.Json!)["status"]!.ToString() == "completed")),
            TimeSpan.FromSeconds(3),
            "Weir reporting the replaced send as completed");
    }

    [Fact]
    public async Task A_resent_release_Weir_processed_normally_is_reported_finished_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var hand = HandoffFor(recorded, 2);
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        EmittedReport.AssertMatches(recorded, 2, (await ReportAsync(world, hand.HandoffId())).Body);
    }

    [Theory]
    [InlineData("reply-no-sign-in", 2)]
    [InlineData("reply-key-without-imports-scope", 0)]
    [InlineData("reply-key-without-imports-scope", 2)]
    public async Task A_report_Deluno_refuses_with_a_401_or_either_403_shape_is_not_retried_and_names_the_key_to_check(string state, int exchange)
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var refusal = DelunoSide.Reply(state);
        // Deluno recorded these refusals for the routes a key without the right permission meets; its report route is one of them.
        world.Deluno.Route("POST", ReportsTo, new Reply(refusal.Exchanges[exchange].Reply.Status, refusal.Exchanges[exchange].Reply.Body?.DeepClone()));
        var hand = DelunoSide.Request("handoff-single-file").Only.Request;
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await world.Replay.SendAsync(DelunoSide.Request("handoff-single-file"));

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(DelunoSide.Request("events-completed"), 0, report.Body);
        var file = FileNameIn((string)report.Body["outputPath"]!);
        var entry = Assert.Single(await world.Scenario.ActivityAsync("processing.handoff_reported"));
        Assert.Equal($"Weir could not tell {Deluno(world)} about {file}", (string)entry["title"]!);
        var detail = JsonNode.Parse((string)entry["detail"]!)!.AsObject();
        Assert.Equal("failed", (string)detail["result"]!);
        Assert.Equal(
            $"Check that {Deluno(world)} is running and that its address and API key are right in Setup › Connections › Media managers.",
            (string)detail["next_action"]!);
        Assert.Equal("completed", (string)(await world.Scenario.HandoffStatusAsync(hand.HandoffId()))["state"]!);
        Assert.Single(world.Deluno.RequestsTo("POST", ReportsTo));
        Assert.Empty(await OwedAsync(world, [hand.HandoffId()]));
    }

    // --- steps ----------------------------------------------------------------------------------------

    /// <summary>What Weir calls the fake Deluno in its sentences: Deluno on the address it was reached at.</summary>
    private static string Deluno(RecordedWorld world) => $"Deluno on {new Uri(world.Deluno.BaseUrl).Host}";

    /// <summary>The hand-off Deluno made for the release a report recording names: the recorded single-file hand-off with that id and release.</summary>
    private static RecordedCall HandoffFor(Recording report, int exchange) =>
        DelunoSide.HandoffLike((string)report.Exchanges[exchange].Request.Body!["handoffId"]!, DelunoSide.ReleaseNameOf(report.Exchanges[exchange]));

    private static async Task HandOffAsync(RecordedWorld world, RecordedCall hand)
    {
        var accepted = await world.Replay.SendCallAsync(hand);
        Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());
    }

    /// <summary>The name of the file at the end of a path as Deluno's Windows machine writes it.</summary>
    private static string FileNameIn(string windowsPath) => windowsPath[(windowsPath.LastIndexOf('\\') + 1)..];

    /// <summary>The film, or the listed films of a release folder, put where the hand-off says they are.</summary>
    private static void PutFilms(RecordedWorld world, RecordedCall hand)
    {
        if (hand.Body!["sourceFiles"] is JsonArray listed)
        {
            foreach (var file in listed)
            {
                world.PutFilm((string)file!, needsWork: true);
            }

            return;
        }

        world.PutFilm(hand.SourcePath(), needsWork: true);
    }

    private sealed record Sent(RecordedRequest Request, JsonObject Body);

    /// <summary>The report Weir sent for the hand-off, once it has.</summary>
    private static async Task<Sent> ReportAsync(RecordedWorld world, string handoffId)
    {
        var request = await Poll.UntilAsync(
            () => Task.FromResult(world.Deluno.RequestsTo("POST", ReportsTo).FirstOrDefault(sent => ((JsonObject)sent.Json!)["handoffId"]!.ToString() == handoffId)),
            $"Weir's report of {handoffId}");
        return new Sent(request, (JsonObject)request.Json!);
    }

    /// <summary>Of these hand-offs, those Weir still holds a report for to send, read from its database while it is stopped.</summary>
    private static async Task<List<string>> OwedAsync(RecordedWorld world, IEnumerable<string> handoffIds)
    {
        await using var database = await world.Scenario.Server.StopForDatabaseAsync();
        var owed = SeedSql.Rows(
            database.Connection,
            "SELECT handoff_id FROM media_manager_handoffs WHERE source_key = 'deluno' AND pending_report_json IS NOT NULL");
        return [.. owed.Select(row => (string)row["handoff_id"]!).Intersect(handoffIds)];
    }
}
