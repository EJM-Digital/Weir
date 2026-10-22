using System.Text.RegularExpressions;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The reports Weir sent Deluno for the endings the first rc.30.30 session did not reach, replayed from the rc.30.32 recordings: two different
/// files with one name under each collision policy, a season pack, a season pack with one episode Weir cannot read, and a release with no
/// video that is rejected. Weir does each for real (its own pass, its own folders, a fake Deluno that answers as Deluno did); the report it
/// sends is held to the recorded one by <see cref="EmittedReport"/>. The hand-offs these were made by are Deluno's recorder's, not recorded:
/// each is the recorded single-file hand-off with the id, release, library and path the report names.
/// </summary>
public sealed partial class DelunoReportsTests
{
    [Fact]
    public async Task A_file_sent_again_under_the_replace_policy_is_not_cleaned_twice_and_is_reported_under_the_name_of_the_first_as_Deluno_recorded() =>
        await CollisionAsync("replace", DelunoSide.Report("events-completed-collision-replace-first"), DelunoSide.Report("events-completed-collision-replace-second"));

    [Fact]
    public async Task A_file_sent_again_under_the_keep_both_policy_is_not_cleaned_twice_and_is_reported_under_the_name_of_the_first_as_Deluno_recorded() =>
        await CollisionAsync("keep_both", DelunoSide.Report("events-completed-collision-keep-both-first"), DelunoSide.Report("events-completed-collision-keep-both-second"));

    private static async Task CollisionAsync(string policy, Recording first, Recording second)
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        world.Deluno.AnswerReportsAsRecorded(world.Translation, first, second);
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("output_collision_policy", policy));
        // Both are sent as the same file, as Deluno's recorder sent them: Weir keeps a source's place below the watched folder, so two
        // folders would never meet in the output, and it has already cleaned a source whose path, size and time it sees again.
        var fileName = DelunoSide.FileNameOf((string)first.Exchanges[0].Request.Body!["outputPath"]!);
        var one = DelunoSide.HandoffFor(first.Exchanges[0]);
        var two = DelunoSide.HandoffFor(second.Exchanges[0], fileName);
        world.PutFilm(one.SourcePath(), needsWork: true);
        await HandOffAsync(world, one);
        EmittedReport.AssertMatches(first, 0, (await ReportAsync(world, world.Translation.Id(one.HandoffId()))).Body, world.Translation);

        // The same file sent again once the first has been cleaned and left in the output.
        await HandOffAsync(world, two);

        EmittedReport.AssertMatches(second, 0, (await ReportAsync(world, world.Translation.Id(two.HandoffId()))).Body, world.Translation);
        Assert.Single(Directory.GetFiles(world.Translation.Apply(DelunoSide.DelunoReadyFolder)));
    }

    [Fact]
    public async Task A_season_pack_of_two_episodes_is_reported_once_with_the_folder_and_both_files_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync(withTv: true);
        var recorded = DelunoSide.Report("events-completed-season-pack");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        var sent = recorded.Exchanges[0].Request.Body!;
        var folder = DelunoSide.FileNameOf((string)sent["outputPath"]!);
        var hand = DelunoSide.PackHandoffFor(recorded.Exchanges[0], folder);
        foreach (var episode in sent["outputFiles"]!.AsArray())
        {
            world.PutFilm($@"{RecordedWorld.RecordedTvWatched}\{folder}\{DelunoSide.FileNameOf((string)episode!)}", needsWork: true);
        }

        await HandOffAsync(world, hand);

        EmittedReport.AssertMatches(recorded, 0, (await ReportAsync(world, world.Translation.Id(hand.HandoffId()))).Body, world.Translation);
    }

    [Fact]
    public async Task A_season_pack_with_one_episode_Weir_cannot_read_is_reported_failed_and_held_with_the_two_files_it_finished_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync(withTv: true);
        var recorded = DelunoSide.Report("events-failed-partial-season-pack");
        var corrupt = DelunoSide.Report("events-failed-partial-season-pack-corrupt");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        await world.Scenario.UpdateLibraryAsync(world.Tv, ("failure_policy", "hold"), ("max_attempts", 2));
        var sent = recorded.Exchanges[0].Request.Body!;
        var finished = sent["outputFiles"]!.AsArray().Select(file => (string)file!).ToList();
        var folder = DelunoSide.FileNameOf(finished[0][..finished[0].LastIndexOf('\\')]);
        var hand = DelunoSide.PackHandoffFor(recorded.Exchanges[0], folder);
        foreach (var episode in finished)
        {
            world.PutFilm($@"{RecordedWorld.RecordedTvWatched}\{folder}\{DelunoSide.FileNameOf(episode)}", needsWork: true);
        }

        // The recording names the episode it could not read in its message. Weir cannot probe it, as it cannot the recorded file.
        var unreadable = Regex.Match((string)sent["message"]!, @"[\w.\-]+S01E03[\w.\-]*\.mkv").Value;
        Assert.NotEmpty(unreadable);
        var bad = world.PutFilm($@"{RecordedWorld.RecordedTvWatched}\{folder}\{unreadable}", needsWork: true);
        Assert.True(File.Exists(bad));
        world.Scenario.FakeTools.SetFileRule(unreadable, new FileRule { ProbeError = "Invalid data found when processing input" });

        await HandOffAsync(world, hand);

        var report = await ReportAsync(world, world.Translation.Id(hand.HandoffId()));
        EmittedReport.AssertMatches(recorded, 0, report.Body, world.Translation);
        EmittedReport.AssertMatches(corrupt, 0, report.Body, world.Translation);
    }

    [Fact]
    public async Task A_release_with_no_video_is_rejected_and_reported_failed_rejected_and_left_for_Deluno_to_remove_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var recorded = DelunoSide.Report("events-failed-rejected");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        world.Deluno.Capabilities.Add("processor-reject-regrab");
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("failure_policy", "reject"));
        // The report names no file, so the file's name is not the recording's: it is what the recorder's other films are called.
        var hand = DelunoSide.HandoffFor(recorded.Exchanges[0], "Recorded.Weir.AudioOnly.mkv");
        var path = world.Translation.Apply(hand.SourcePath());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(FakeMedia.Probe(video: 0, audioLanguages: ["eng"])));

        await HandOffAsync(world, hand);

        EmittedReport.AssertMatches(recorded, 0, (await ReportAsync(world, world.Translation.Id(hand.HandoffId()))).Body, world.Translation);
        Assert.True(File.Exists(path), "Deluno removes the download, Weir leaves it");
    }

    [Fact]
    public async Task A_release_with_no_video_under_the_reject_policy_is_handed_back_unchanged_and_reported_finished_when_Deluno_cannot_regrab_as_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartReportsWorldAsync();
        var recorded = DelunoSide.Report("events-completed-reject-policy-");
        world.Deluno.AnswerReportsAsRecorded(world.Translation, recorded);
        // The policy was chosen while Deluno said it could replace a rejected release; by the time the file came, it no longer did.
        world.Deluno.Capabilities.Add("processor-reject-regrab");
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("failure_policy", "reject"));
        world.Deluno.Capabilities.Replace(world.Deluno.Capabilities.Snapshot().Where(name => name != "processor-reject-regrab"));
        var hand = DelunoSide.HandoffFor(recorded.Exchanges[0], "Recorded.Weir.AudioOnly.mkv");
        var path = world.Translation.Apply(hand.SourcePath());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(FakeMedia.Probe(video: 0, audioLanguages: ["eng"])));

        await HandOffAsync(world, hand);

        EmittedReport.AssertMatches(recorded, 0, (await ReportAsync(world, world.Translation.Id(hand.HandoffId()))).Body, world.Translation);
        Assert.Equal("passed-through", (string)(await world.Scenario.HandoffStatusAsync(world.Translation.Id(hand.HandoffId())))["state"]!);
        Assert.True(File.Exists(world.Translation.Apply((string)recorded.Exchanges[0].Request.Body!["outputPath"]!)), "Weir handed the original back");
    }
}
