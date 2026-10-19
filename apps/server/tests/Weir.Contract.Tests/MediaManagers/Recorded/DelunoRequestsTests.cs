using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoCalls;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What the real Deluno (rc.30.30) sent Weir, replayed against a Weir set up the way that session had it: its hand-offs of a single
/// file, a release folder, a Usenet folder and a release sent twice; its status polls, its cancel, its outcomes, and its questions about
/// what Weir can do and which folders it watches. Each reply is held to the one Deluno recorded, and what Weir holds afterwards is what the
/// call implies. A hand-off a recording does not hold is made from the recorded one with only its id and release changed
/// (<see cref="DelunoSide.HandoffLike"/>).
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoRequestsTests
{
    private const string Ready = DelunoSide.DelunoReadyFolder;

    /// <summary>
    /// #955: the answer to the outcome of the second send was recorded (Weir 1.0.0-rc.13) carrying the first send's refusal, "Deluno will
    /// not import this file", and keeping a copy the import had just made redundant. An outcome is about the hand-off it names.
    /// </summary>
    private static readonly ExpectedDifference[] ReplyAboutOnlyTheNewSend =
    [
        new("#955", "$.released", "the import of the second send releases Weir's copy, whatever the first send was refused for", JsonValue.Create(true)),
        new(
            "#955",
            "$.message",
            "the reply carries only the note of the hand-off the outcome names",
            JsonValue.Create("Weir recorded that Deluno imported the file and released its copy.")),
    ];

    // --- what Weir can do ----------------------------------------------------------------------------

    [Fact]
    public async Task Deluno_asking_what_Weir_can_do_gets_the_capabilities_it_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();

        await world.Replay.SendAsync(DelunoSide.Request("capabilities-ask"));
    }

    // --- hand-offs -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_single_file_in_the_watched_folder_is_accepted_and_cleaned_into_the_output_folder_without_a_folder_of_its_own()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-single-file");
        var hand = recording.Only.Request;
        var source = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));

        await world.Replay.SendAsync(recording);

        var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        Assert.Equal($@"{Ready}\Gprecsingleemdq01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", (string)done["outputPath"]!);
        var output = world.Translation.Apply((string)done["outputPath"]!);
        Assert.True(File.Exists(output), $"{output} was not written");
        source.AssertStill();
    }

    [Fact]
    public async Task A_release_folder_is_accepted_and_only_the_files_it_lists_are_cleaned_into_a_folder_named_for_the_release()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-release-folder");
        var hand = recording.Only.Request;
        var listed = hand.Body!["sourceFiles"]!.AsArray().Select(file => (string)file!).ToList();
        var sources = listed.Select(file => Untouched.Of(world.PutFilm(file, needsWork: true))).ToList();
        var unlisted = world.PutFilm($@"{hand.SourcePath()}\Not.Listed.mkv", needsWork: true);

        await world.Replay.SendAsync(recording);

        var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        var release = "Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN";
        Assert.Equal($@"{Ready}\{release}\{release}.mkv", (string)done["outputPath"]!);
        var output = world.Translation.Apply((string)done["outputPath"]!);
        Assert.True(File.Exists(output), $"{output} was not written");
        Assert.Equal([release], Directory.GetDirectories(world.Translation.Apply(Ready)).Select(Path.GetFileName));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(output)!));
        Assert.True(File.Exists(unlisted), "the file the hand-off did not list is still where it was");
        sources.ForEach(source => source.AssertStill());
    }

    [Fact]
    public async Task A_Usenet_job_folder_is_accepted_and_cleaned_into_a_folder_named_for_the_release()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-usenet-folder");
        var hand = recording.Only.Request;
        var source = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));

        await world.Replay.SendAsync(recording);

        var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        var release = "Gprecusenetekhf03.2020.1080p.WEB-DL.x264-GOLDEN";
        Assert.Equal($@"{Ready}\{release}\{release}.mkv", (string)done["outputPath"]!);
        var output = world.Translation.Apply((string)done["outputPath"]!);
        Assert.True(File.Exists(output), $"{output} was not written");
        source.AssertStill();
    }

    [Fact]
    public async Task A_release_sent_again_after_it_was_cleaned_is_accepted_under_its_new_id_and_not_cleaned_twice()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-resend");
        var first = recording.Exchanges[0].Request;
        var again = recording.Exchanges[1].Request;
        var source = Untouched.Of(world.PutFilm(first.SourcePath(), needsWork: true));
        Assert.Equal(first.SourcePath(), again.SourcePath());

        await world.Replay.SendAsync(recording, 0);
        await world.Scenario.WaitForHandoffStateAsync(first.HandoffId(), "completed");
        await world.Replay.SendAsync(recording, 1);

        var resent = await world.Scenario.WaitForHandoffStateAsync(again.HandoffId(), "completed");
        Assert.Equal($@"{Ready}\Gprecbeforedage01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", (string)resent["outputPath"]!);
        Assert.Single(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(Directory.GetFiles(world.Translation.Apply(Ready)));
        source.AssertStill();
    }

    // --- the status poll and the cancel of a hand-off waiting for its turn -----------------------------

    [Fact]
    public async Task A_hand_off_Weir_holds_queued_while_paused_reads_queued_in_position_one_each_time_Deluno_asks()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("status-ask-queued");
        var hand = await HandOffQueuedAsync(world, recording.Exchanges[0].Request.HandoffIdInPath(), "Gprecqueuedxrbw01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        Assert.Equal("queued", await StateOfAsync(world, hand.HandoffId()));
    }

    [Fact]
    public async Task Stop_waiting_cancels_a_queued_hand_off_with_a_204_and_the_work_is_never_done()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("cancel-queued");
        var films = new List<Untouched>();
        var ids = new List<string>();
        foreach (var (exchange, release) in recording.Exchanges.Zip(["Gprecstopaxkd01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "Gprecstopbyme02.2020.1080p.WEB-DL.x264-GOLDEN.mkv"]))
        {
            var hand = await HandOffQueuedAsync(world, exchange.Request.HandoffIdInPath(), release);
            ids.Add(hand.HandoffId());
            films.Add(Untouched.Of(world.Translation.Apply(hand.SourcePath())));
        }

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        foreach (var id in ids)
        {
            Assert.Equal("cancelled", await StateOfAsync(world, id));
            Assert.Equal("cancelled", await RemuxJobs.StatusForHandoffAsync(world.Scenario.Admin, id));
        }

        Assert.Equal(
            ["Deluno cancelled its hand-off of Gprecstopaxkd01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "Deluno cancelled its hand-off of Gprecstopbyme02.2020.1080p.WEB-DL.x264-GOLDEN.mkv"],
            (await world.ActivityTitlesAsync("processing.handoff_cancelled")).Order());
        Assert.Empty(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        films.ForEach(film => film.AssertStill());
    }

    // --- what Deluno says became of the cleaned file ----------------------------------------------------

    [Fact]
    public async Task Deluno_reporting_it_moved_Weirs_copy_into_its_library_is_recorded_as_imported_with_nothing_left_to_remove()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("outcome-imported");
        var single = DelunoSide.Request("handoff-single-file").Only.Request;
        var folder = DelunoSide.Request("handoff-release-folder").Only.Request;
        await FinishedAsync(world, single, folder);
        // Deluno moved the copies into its library, so Weir's folder no longer holds them.
        File.Delete(world.Translation.Apply($@"{Ready}\Gprecsingleemdq01.2020.1080p.WEB-DL.x264-GOLDEN.mkv"));
        File.Delete(world.Translation.Apply($@"{Ready}\Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN\Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN.mkv"));

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        var handback = await world.HandbackAsync("Gprecsingleemdq01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        var inFolder = await world.HandbackAsync("Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN/Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)inFolder["outcome"]!, (string)inFolder["outcome_by"]!));
        Assert.Equal(
            ["Deluno imported Gprecfoldervpli02.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "Deluno imported Gprecsingleemdq01.2020.1080p.WEB-DL.x264-GOLDEN.mkv"],
            (await world.ActivityTitlesAsync("processing.handback_outcome")).Order());
    }

    [Fact]
    public async Task Deluno_saying_it_will_not_import_a_hand_off_it_removed_is_recorded_and_Weir_keeps_its_copy()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("outcome-not-imported-removed");
        var first = DelunoSide.RecordedHandoff(recording.Exchanges[0].Request.HandoffIdInPath())!;
        var during = DelunoSide.HandoffLike(recording.Exchanges[1].Request.HandoffIdInPath(), "Gprecduringdoqh02.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        await FinishedAsync(world, first, during);

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        foreach (var file in new[] { "Gprecbeforedage01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "Gprecduringdoqh02.2020.1080p.WEB-DL.x264-GOLDEN.mkv" })
        {
            var handback = await world.HandbackAsync(file);
            Assert.Equal(("not-imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
            Assert.Null(handback["released_at"]);
            Assert.True(File.Exists(world.Translation.Apply($@"{Ready}\{file}")), $"Weir kept its copy of {file}");
        }
    }

    [Fact]
    public async Task Deluno_importing_the_resend_of_a_release_it_removed_is_recorded_as_imported_for_the_new_send()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var sends = DelunoSide.Request("handoff-resend");
        var removed = DelunoSide.Request("outcome-not-imported-removed");
        var imported = DelunoSide.Request("outcome-imported-for-replaced-send");
        world.PutFilm(sends.Exchanges[0].Request.SourcePath(), needsWork: true);
        await world.Replay.SendAsync(sends, 0);
        await world.Scenario.WaitForHandoffStateAsync(sends.Exchanges[0].Request.HandoffId(), "completed");
        await world.Replay.SendAsync(removed, 0);
        await world.Replay.SendAsync(sends, 1);
        await world.Scenario.WaitForHandoffStateAsync(sends.Exchanges[1].Request.HandoffId(), "completed");

        await world.Replay.SendAsync(imported, ReplyAboutOnlyTheNewSend);

        var handback = await world.HandbackAsync("Gprecbeforedage01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.False(File.Exists(world.Translation.Apply($@"{Ready}Gprecbeforedage01.2020.1080p.WEB-DL.x264-GOLDEN.mkv")), "Deluno has the file, so Weir released its copy");
        Assert.DoesNotContain("will not import", (string?)handback["release_note"] ?? string.Empty, StringComparison.Ordinal);
    }

    // --- steps ----------------------------------------------------------------------------------------

    /// <summary>Each hand-off made, with its film in place, and left until Weir has finished it.</summary>
    private static async Task FinishedAsync(RecordedWorld world, params RecordedCall[] hands)
    {
        foreach (var hand in hands)
        {
            if (hand.Body!["sourceFiles"] is JsonArray listed)
            {
                foreach (var file in listed)
                {
                    world.PutFilm((string)file!, needsWork: true);
                }
            }
            else
            {
                world.PutFilm(hand.SourcePath(), needsWork: true);
            }

            var accepted = await world.Replay.SendCallAsync(hand);
            Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());
            await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        }
    }
}
