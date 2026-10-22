using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What Weir tells Deluno, held to what the real Weir (rc.13) told the real Deluno (rc.30.30) for a hand-off that finished, one that failed,
/// one whose download was gone, and one Deluno had stopped waiting for: the same members in the same order, of the same types, with the
/// exact words <c>status</c>, <c>failureClass</c> (<c>source_gone</c>) and <c>disposition</c>. A fake Deluno records what Weir POSTs and
/// answers with the reply Deluno recorded for that report; Weir must read a 202 as delivered and a 409 as Deluno having stopped waiting.
/// </summary>
[ContractArea("media_managers")]
public sealed partial class DelunoReportsTests
{
    private const string ReportsTo = Scenario.EventsPath;

    [Fact]
    public async Task A_finished_single_file_release_folder_and_Usenet_folder_are_each_reported_as_Deluno_recorded_and_the_202_settles_them()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var handoffs = recorded.Exchanges.Select(report => DelunoSide.RecordedHandoff((string)report.Request.Body!["handoffId"]!)!).ToList();

        var ids = new List<string>();
        for (var index = 0; index < handoffs.Count; index++)
        {
            var hand = handoffs[index];
            PutFilms(world, hand);
            var accepted = await world.Replay.SendCallAsync(hand);
            Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());

            var report = await ReportAsync(world, hand.HandoffId());

            EmittedReport.AssertMatches(recorded, index, report.Body);
            Assert.Equal(world.Deluno.ApiKey, report.Request.Header("X-Api-Key"));
            Assert.Equal("application/json", report.Request.Header("Content-Type")?.Split(';')[0]);
            ids.Add(hand.HandoffId());
        }

        Assert.Equal(
            recorded.Exchanges.Select(exchange => $"Told {Deluno(world)} that {FileNameIn((string)exchange.Request.Body!["outputPath"]!)} is ready to import").Order(),
            (await RecordedReportsAsync(world, handoffs.Count)).Select(entry => (string)entry["title"]!).Order());
        Assert.Empty(await OwedAsync(world, ids));
    }

    [Fact]
    public async Task A_file_under_the_workflows_minimum_size_is_reported_failed_and_held_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-failed-under-minimum");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("min_file_size_mb", 50));
        // The report names no file: the file's name is what the recorder's other films are called.
        var hand = HandoffFor(recorded, 0, "Recorded.Weir.tiny.mkv");
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, world.Translation.Id(hand.HandoffId()));
        EmittedReport.AssertMatches(recorded, 0, report.Body, world.Translation);
    }

    [Fact]
    public async Task A_file_Weir_cannot_read_is_reported_failed_and_held_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-failed-unreadable");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        var hand = HandoffFor(recorded, 0, "Recorded.Weir.unreadable.mkv");
        var junk = new byte[256 * 1024];
        new Random(971).NextBytes(junk);
        var path = world.Translation.Apply(hand.SourcePath());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, junk);
        world.Scenario.FakeTools.SetFileRule(Path.GetFileName(path), new FileRule { ProbeError = "Invalid data found when processing input" });

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, world.Translation.Id(hand.HandoffId()));
        EmittedReport.AssertMatches(recorded, 0, report.Body, world.Translation);
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
        await MakeSecondLookDueAsync(world);

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
    }

    [Fact]
    public async Task A_held_file_that_is_back_when_Deluno_sends_the_release_again_is_cleaned_at_once_and_reported_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var first = FirstSendOf(recorded, 1);
        var again = HandoffFor(recorded, 1);
        await HoldAsGoneAsync(world, first);
        var booked = await BookedLookAsync(world);

        world.PutFilm(again.SourcePath(), needsWork: true);
        await HandOffAsync(world, again);

        // The booked look is minutes away, and nothing here moves it: the resend itself started the look.
        var report = await ReportAsync(world, again.HandoffId());
        EmittedReport.AssertMatches(recorded, 1, report.Body);
        Assert.True(string.CompareOrdinal(booked, Handoffs.StoredTime(DateTime.UtcNow)) > 0, "the look was not booked for later");
        Assert.Equal("completed", (string)(await world.Scenario.WaitForHandoffStateAsync(again.HandoffId(), "completed"))["state"]!);
        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(first.HandoffId()))["state"]!);
        Assert.Empty(await BookedLooksAsync(world));
    }

    [Fact]
    public async Task A_held_file_that_comes_back_on_its_own_is_cleaned_when_Weir_next_looks_and_reported_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var single = SingleFileReport(recorded);
        var hand = HandoffFor(recorded, single);
        await HoldAsGoneAsync(world, hand);
        var booked = await BookedLookAsync(world);

        // The file is back, and nothing tells Weir so: it finds out when it next looks for vanished files, which it does as it starts.
        world.PutFilm(hand.SourcePath(), needsWork: true);
        await world.Scenario.RestartServerAsync();

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(recorded, single, report.Body);
        Assert.True(string.CompareOrdinal(booked, Handoffs.StoredTime(DateTime.UtcNow)) > 0, "the look was not booked for later");
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        Assert.Empty(await BookedLooksAsync(world));
    }

    [Fact]
    public async Task A_report_of_a_release_Deluno_removed_is_answered_409_dismissed_and_Weir_stops_owing_it()
    {
        await using var world = await DelunoSide.StartFirstSessionWorldAsync();
        var tooLate = DelunoSide.FirstSession("events-too-late");
        world.Deluno.AnswerReportsAsRecorded(tooLate);
        var sends = DelunoSide.FirstSession("handoff-resend");
        var first = sends.Exchanges[0].Request;
        world.PutFilm(first.SourcePath(), needsWork: true);

        await world.Replay.SendAsync(sends, 0);

        var report = await ReportAsync(world, first.HandoffId());
        EmittedReport.AssertMatches(tooLate, 0, report.Body);

        // Deluno answered, so Weir does not say it could not reach it or that its address or key are wrong: it says Deluno had already
        // stopped waiting, and that no import was queued, and does not ask a person to check anything.
        var file = DelunoSide.ReleaseNameOf(tooLate.Exchanges[0]);
        var entry = Assert.Single(await RecordedReportsAsync(world, 1));
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
    public async Task A_release_sent_again_after_it_was_cleaned_is_reported_finished_with_the_copy_made_before_and_says_it_cleaned_nothing_twice_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-already-cleaned");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var sends = DelunoSide.Request("handoff-resend-during");
        world.PutFilm(sends.Exchanges[0].Request.SourcePath(), needsWork: true);
        await world.Replay.SendAsync(sends, 0);
        await ReportAsync(world, sends.Exchanges[0].Request.HandoffId());
        await world.Replay.SendAsync(DelunoSide.Request("outcome-not-imported-removed"), 0);

        await world.Replay.SendAsync(sends, 1);

        var report = await ReportAsync(world, sends.Exchanges[1].Request.HandoffId());
        EmittedReport.AssertMatches(recorded, 0, report.Body);
        Assert.Single(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
    }

    [Fact]
    public async Task A_release_sent_again_while_the_first_send_is_in_Weir_is_reported_finished_once_for_the_new_send_only()
    {
        await using var world = await DelunoSide.StartFirstSessionWorldAsync();
        var recorded = DelunoSide.FirstSession("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var release = world.HoldEveryPass();
        var first = HandoffFor(DelunoSide.FirstSession("events-too-late"), 1);
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
        // One worker takes the passes in the order they were queued, so the replaced send's pass ended before the report for the send that
        // replaced it, which has arrived: a report for the first would have left before it.
        Assert.DoesNotContain(
            world.Deluno.RequestsTo("POST", ReportsTo),
            sent => ((JsonObject)sent.Json!)["handoffId"]!.ToString() == first.HandoffId() && ((JsonObject)sent.Json!)["status"]!.ToString() == "completed");
    }

    [Fact]
    public async Task A_resent_release_Weir_processed_normally_is_reported_finished_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recorded = DelunoSide.Request("events-completed-resent");
        world.Deluno.AnswerReportsAsRecorded(recorded);
        var hand = HandoffFor(recorded, 0);
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await HandOffAsync(world, hand);

        EmittedReport.AssertMatches(recorded, 0, (await ReportAsync(world, hand.HandoffId())).Body);
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
        var completed = DelunoSide.Request("events-completed");
        var single = SingleFileReport(completed);
        var hand = DelunoSide.RecordedHandoff((string)completed.Exchanges[single].Request.Body!["handoffId"]!)!;
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await world.Replay.SendCallAsync(hand);

        var report = await ReportAsync(world, hand.HandoffId());
        EmittedReport.AssertMatches(completed, single, report.Body);
        var file = FileNameIn((string)report.Body["outputPath"]!);
        var entry = Assert.Single(await RecordedReportsAsync(world, 1));
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
    private static RecordedCall HandoffFor(Recording report, int exchange, string? fileName = null)
    {
        var hand = DelunoSide.HandoffLike((string)report.Exchanges[exchange].Request.Body!["handoffId"]!, DelunoSide.ReleaseNameOf(report.Exchanges[exchange]), fileName);
        hand.Body!["libraryId"] = (string)report.Exchanges[exchange].Request.Body!["libraryId"]!;
        return hand;
    }

    /// <summary>The report of a single file (its release is the file's name) among the reports of finished releases.</summary>
    private static int SingleFileReport(Recording completed) =>
        completed.Exchanges.ToList().FindIndex(report => DelunoSide.ReleaseNameOf(report).EndsWith(".mkv", StringComparison.Ordinal));

    /// <summary>
    /// The first send of the release a report of its second send names, as Deluno recorded the report of the first being replaced: the hand-off
    /// of the recording of replaced sends that has the same release.
    /// </summary>
    private static RecordedCall FirstSendOf(Recording report, int exchange)
    {
        var release = DelunoSide.ReleaseNameOf(report.Exchanges[exchange]);
        var superseded = DelunoSide.Report("events-failed-superseded");
        var replaced = superseded.Exchanges.Single(sent => DelunoSide.ReleaseNameOf(sent) == release);
        return DelunoSide.RecordedHandoff((string)replaced.Request.Body!["handoffId"]!) ?? HandoffFor(superseded, superseded.Exchanges.ToList().IndexOf(replaced));
    }

    /// <summary>Hands the release off while the file is there, takes the file away, and waits until Weir holds it as gone with a look booked for later.</summary>
    private static async Task HoldAsGoneAsync(RecordedWorld world, RecordedCall hand)
    {
        await world.Scenario.SetPauseAsync(paused: true);
        var path = world.PutFilm(hand.SourcePath(), needsWork: true);
        await HandOffAsync(world, hand);
        File.Delete(path);
        await world.Scenario.SetPauseAsync(paused: false);
        await Poll.UntilAsync(
            async () => (bool?)(await world.Scenario.FileRowAsync(world.Movies, Path.GetFileName(path)))?["source_gone"] is true ? "held as gone" : null,
            "Weir to hold the file as gone after its first look");
    }

    /// <summary>When the look at the gone file is booked for, once it is. The file shows as gone a moment before the look is booked, so the server is asked again until it is there.</summary>
    private static async Task<string> BookedLookAsync(RecordedWorld world) =>
        await Poll.UntilAsync(async () => (await BookedLooksAsync(world)).FirstOrDefault(), "Weir to book a look at the gone file");

    private static async Task<List<string>> BookedLooksAsync(RecordedWorld world) =>
        [.. (await world.Scenario.RowsAfterStopAsync("SELECT not_before FROM jobs WHERE status = 'pending' AND not_before IS NOT NULL AND dedupe_key LIKE '%:gone-wait:%'"))
            .Select(row => (string)row["not_before"]!)];

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

    /// <summary>
    /// The Activity lines for the reports Weir has settled, once there are <paramref name="count"/>. Weir records a report, and stops owing
    /// it, after the manager has answered it, in one write; the manager has the report before that, so a test that reads either as soon
    /// as the manager has the report reads them too early. The wait lasts as long as a worker has the job (see <see cref="JobWait"/>).
    /// </summary>
    private static async Task<List<JsonObject>> RecordedReportsAsync(RecordedWorld world, int count) =>
        await world.Scenario.WaitWhileWorkedOnAsync(
            async () => await world.Scenario.ActivityAsync("processing.handoff_reported") is { } lines && lines.Count >= count ? lines : null,
            $"Weir to record {count} report(s) in Activity");

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
