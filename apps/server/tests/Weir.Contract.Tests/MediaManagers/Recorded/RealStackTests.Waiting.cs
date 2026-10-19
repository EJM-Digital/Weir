using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

public sealed partial class RealStackTests
{
    [Fact]
    public async Task A_download_deleted_while_Weir_holds_the_hand_off_is_answered_queued_with_the_reason_and_then_reported_gone_as_they_said()
    {
        var recording = RealStackSide.Load("real-deleted-before-cleaning");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var ask = RealStackSide.IndexOf(recording, exchange => exchange.Request.Method == "GET", "status ask");
        var report = RealStackSide.ReportIndex(recording, "failed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        await world.Scenario.SetPauseAsync(paused: true);
        var paths = RealStackSide.SourceFilesOf(hand).Select(file => world.PutFilm(file, needsWork: true)).ToList();
        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));
        paths.ForEach(File.Delete);

        await world.Scenario.SetPauseAsync(paused: false);
        await Poll.UntilAsync(
            async () => (bool?)(await world.Scenario.FileRowAsync(world.Movies, $"{Path.GetFileName(hand.Request.SourcePath())}/{Path.GetFileName(paths[0])}"))?["source_gone"] is true ? "held as gone" : null,
            "Weir to hold the file as gone after its first look");
        // Deluno asked while the file was gone and the look was not yet final: the hand-off is still queued, and Weir says why.
        await world.Replay.SendAsync(recording, ask);

        // The look a vanished file waits for is minutes away; it is made due now, as it would be once that time has passed.
        await MakeSecondLookDueAsync(world);
        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, hand.Request.HandoffId())).Body);
    }

    [Fact]
    public async Task A_hand_off_the_person_stopped_waiting_for_is_dropped_with_a_204_and_is_never_worked()
    {
        var recording = RealStackSide.Load("real-stop-waiting");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var cancel = RealStackSide.IndexOf(recording, exchange => exchange.Request.Method == "DELETE", "cancel");
        await world.Scenario.SetPauseAsync(paused: true);
        var paths = RealStackSide.SourceFilesOf(hand).Select(file => world.PutFilm(file, needsWork: true)).ToList();
        var untouched = paths.Select(Untouched.Of).ToList();
        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        await world.Replay.SendAsync(recording, cancel);

        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(hand.Request.HandoffId()))["state"]!);
        await world.Scenario.SetPauseAsync(paused: false);
        await world.Scenario.WaitForHandoffStateAsync(hand.Request.HandoffId(), "cancelled");
        Assert.Empty(Directory.GetFiles(world.Scenario.Folders.Output, "*", SearchOption.AllDirectories));
        untouched.ForEach(source => source.AssertStill());
        Assert.Contains(
            $"Deluno cancelled its hand-off of {Path.GetFileNameWithoutExtension(RealStackSide.SourceFilesOf(hand).Single())}",
            await world.ActivityTitlesAsync("processing.handoff_cancelled"),
            StringComparer.Ordinal);
    }
}
