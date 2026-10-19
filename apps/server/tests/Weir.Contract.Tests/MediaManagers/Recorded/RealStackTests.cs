using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What a real Deluno and a real Weir said to each other in Compose, replayed against Weir: the recording proxy between them wrote down the
/// hand-off of a release folder, of a folder with a small extra, of a file Weir cannot read, of a release it rejects, of one whose download was
/// deleted while Weir held it and of one the person stopped waiting for. Deluno's calls are sent as recorded and Weir's replies held to the
/// recorded ones; the report Weir makes is held to the one it made then (<see cref="EmittedReport"/>), answered by a fake Deluno as the real one
/// answered.
/// </summary>
[ContractArea("media_managers")]
public sealed partial class RealStackTests
{
    [Fact]
    public async Task A_release_folder_the_real_Deluno_handed_over_is_cleaned_reported_and_its_import_recorded_as_they_said()
    {
        var recording = RealStackSide.Load("real-release-folder");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var report = RealStackSide.ReportIndex(recording, "completed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        foreach (var file in RealStackSide.SourceFilesOf(hand))
        {
            world.PutFilm(file, needsWork: true);
        }

        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        var id = hand.Request.HandoffId();
        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, id)).Body);
        var done = await world.Scenario.WaitForHandoffStateAsync(id, "completed");
        var output = world.Translation.Apply((string)done["outputPath"]!);
        Assert.Equal((string)recording.Exchanges[report].Request.Body!["outputPath"]!, (string)done["outputPath"]!);
        Assert.True(File.Exists(output), $"{output} was not written");

        // Deluno imported by moving Weir's copy into its library, so there is no copy of Weir's left to release when it says so.
        File.Delete(output);
        await world.Replay.SendAsync(recording, RealStackSide.OutcomeIndex(recording));
    }

    [Fact]
    public async Task A_small_extra_the_real_Deluno_left_out_of_its_hand_off_stays_in_the_download_untouched()
    {
        var recording = RealStackSide.Load("real-release-with-extra");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("min_file_size_mb", 1));
        var hand = RealStackSide.HandoffOf(recording);
        var report = RealStackSide.ReportIndex(recording, "completed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        var listed = RealStackSide.SourceFilesOf(hand);
        var film = Assert.Single(listed);
        var folder = hand.Request.SourcePath();
        var filmPath = world.Translation.Apply(film);
        Directory.CreateDirectory(Path.GetDirectoryName(filmPath)!);
        await File.WriteAllBytesAsync(filmPath, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]), padding: 2 * 1024 * 1024));
        var extra = world.Translation.Apply($"{folder}/Extras/Featurette.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        await File.WriteAllBytesAsync(extra, FakeMedia.Bytes(FakeMedia.Probe()));
        var extraBytes = await File.ReadAllBytesAsync(extra);

        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        var id = hand.Request.HandoffId();
        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, id)).Body);
        await world.Scenario.WaitForHandoffStateAsync(id, "completed");
        Assert.Equal(extraBytes, await File.ReadAllBytesAsync(extra));
        Assert.Empty(Directory.GetFiles(world.Scenario.Folders.Output, "Featurette*", SearchOption.AllDirectories));
        Assert.Null(await world.Scenario.FileRowAsync(world.Movies, $"{Path.GetFileName(folder)}/Extras/Featurette.mkv"));
    }

    [Fact]
    public async Task A_file_Weir_cannot_read_is_reported_failed_and_held_as_the_real_Weir_reported_it_to_the_real_Deluno()
    {
        var recording = RealStackSide.Load("real-failed-unreadable");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var report = RealStackSide.ReportIndex(recording, "failed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        var path = world.Translation.Apply(Assert.Single(RealStackSide.SourceFilesOf(hand)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var zeros = new byte[256 * 1024];
        await File.WriteAllBytesAsync(path, zeros);
        world.Scenario.FakeTools.SetFileRule(Path.GetFileName(path), new FileRule { ProbeError = "Invalid data found when processing input" });

        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, hand.Request.HandoffId())).Body);
        Assert.Equal(zeros, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_release_with_no_video_is_rejected_and_the_real_Deluno_answers_the_report_by_refusing_it_and_looking_for_another()
    {
        var recording = RealStackSide.Load("real-rejected-audio-only");
        await using var world = await RealStackSide.StartWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var report = RealStackSide.ReportIndex(recording, "failed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        world.Deluno.Capabilities.Add("processor-reject-regrab");
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("failure_policy", "reject"));
        var film = RealStackSide.SourceFilesOf(hand).Single();
        var path = world.Translation.Apply(film);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(FakeMedia.Probe(video: 0, audioLanguages: ["eng"])));

        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, hand.Request.HandoffId())).Body);
        Assert.True(File.Exists(path), "Deluno removes the download, Weir leaves it");
        // Weir writes Activity after the manager has the report, so the line is awaited rather than read the moment the report is seen.
        var rejected = await world.Scenario.WaitWhileWorkedOnAsync(
            async () => await world.Scenario.ActivityAsync("processing.file_rejected") is { Count: > 0 } lines ? lines : null,
            "Activity to say the release was rejected");
        Assert.Equal($"{Path.GetFileName(film)} was rejected so a different release can be found", (string)Assert.Single(rejected)["title"]!);
        Assert.Contains("will remove the download; Weir left it in place.", (string)Assert.Single(rejected)["detail"]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("real-tv-episode")]
    [InlineData("real-season-pack")]
    public async Task A_show_the_real_Deluno_handed_over_is_cleaned_in_the_TV_workflow_reported_and_its_unmatched_import_recorded_as_they_said(string state)
    {
        var recording = RealStackSide.Load(state);
        await using var world = await RealStackSide.StartTvWorldAsync(recording);
        var hand = RealStackSide.HandoffOf(recording);
        var report = RealStackSide.ReportIndex(recording, "completed");
        world.Deluno.AnswerReportAs(recording.Exchanges[report]);
        var listed = RealStackSide.SourceFilesOf(hand);
        var sources = listed.Select(file => Untouched.Of(world.PutFilm(file, needsWork: true))).ToList();

        await world.Replay.SendAsync(recording, recording.Exchanges.ToList().IndexOf(hand));

        var id = hand.Request.HandoffId();
        EmittedReport.AssertMatches(recording, report, (await ReportAsync(world, id)).Body);
        var done = await world.Scenario.WaitForHandoffStateAsync(id, "completed");
        Assert.Equal((string)recording.Exchanges[report].Request.Body!["outputPath"]!, (string)done["outputPath"]!);
        foreach (var file in (JsonArray)recording.Exchanges[report].Request.Body!["outputFiles"]!)
        {
            Assert.True(File.Exists(world.Translation.Apply((string)file!)), $"{file} was not written");
        }

        // Deluno could not match the file to an episode (it knows none here), so it will not import; Weir keeps its copy and says so.
        await world.Replay.SendAsync(recording, RealStackSide.OutcomeIndex(recording));
        sources.ForEach(source => source.AssertStill());
    }
}
