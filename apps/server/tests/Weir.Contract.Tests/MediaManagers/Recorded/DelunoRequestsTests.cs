using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoCalls;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What the real Deluno (rc.30.34) sent Weir, replayed against a Weir set up the way that session had it: its hand-offs of a single
/// file, a release folder, a Usenet folder and a release sent twice; its status polls, its cancel, its outcomes, and its questions about
/// what Weir can do and which folders it watches. Each reply is held to the one Deluno recorded, and what Weir holds afterwards is what the
/// call implies. A hand-off a recording does not hold is made from the recorded one with only its id and release changed
/// (<see cref="DelunoSide.HandoffLike"/>).
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoRequestsTests
{
    private const string Ready = DelunoSide.DelunoReadyFolder;

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
        for (var index = 0; index < recording.Exchanges.Count; index++)
        {
            var hand = recording.Exchanges[index].Request;
            var source = Untouched.Of(world.PutFilm(hand.SourcePath(), needsWork: true));

            await world.Replay.SendAsync(recording, index);

            var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
            Assert.Equal($@"{Ready}\{DelunoSide.FileNameOf(hand.SourcePath())}", (string)done["outputPath"]!);
            var output = world.Translation.Apply((string)done["outputPath"]!);
            Assert.True(File.Exists(output), $"{output} was not written");
            source.AssertStill();
        }
    }

    [Fact]
    public async Task A_release_folder_is_accepted_and_only_the_files_it_lists_are_cleaned_into_a_folder_named_for_the_release()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-release-folder");
        var releases = new List<string>();
        for (var index = 0; index < recording.Exchanges.Count; index++)
        {
            var hand = recording.Exchanges[index].Request;
            var listed = hand.Body!["sourceFiles"]!.AsArray().Select(file => (string)file!).ToList();
            var sources = listed.Select(file => Untouched.Of(world.PutFilm(file, needsWork: true))).ToList();
            var unlisted = world.PutFilm($@"{hand.SourcePath()}\Not.Listed.mkv", needsWork: true);

            await world.Replay.SendAsync(recording, index);

            var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
            var release = (string)hand.Body["releaseName"]!;
            releases.Add(release);
            Assert.Equal($@"{Ready}\{release}\{release}.mkv", (string)done["outputPath"]!);
            var output = world.Translation.Apply((string)done["outputPath"]!);
            Assert.True(File.Exists(output), $"{output} was not written");
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(output)!));
            Assert.True(File.Exists(unlisted), "the file the hand-off did not list is still where it was");
            sources.ForEach(source => source.AssertStill());
        }

        Assert.Equal(releases.Order(), Directory.GetDirectories(world.Translation.Apply(Ready)).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_Usenet_job_folder_is_accepted_and_cleaned_into_a_folder_named_for_the_release()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("handoff-usenet-folder");
        for (var index = 0; index < recording.Exchanges.Count; index++)
        {
            // SABnzbd names the file inside its job folder; NZBGet names the folder, and the file inside it is the release's own.
            var hand = recording.Exchanges[index].Request;
            var release = (string)hand.Body!["releaseName"]!;
            var file = hand.SourcePath().EndsWith(".mkv", StringComparison.Ordinal) ? hand.SourcePath() : $@"{hand.SourcePath()}\{release}.mkv";
            var source = Untouched.Of(world.PutFilm(file, needsWork: true));

            await world.Replay.SendAsync(recording, index);

            var done = await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
            Assert.Equal($@"{Ready}\{release}\{release}.mkv", (string)done["outputPath"]!);
            var output = world.Translation.Apply((string)done["outputPath"]!);
            Assert.True(File.Exists(output), $"{output} was not written");
            source.AssertStill();
        }
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
        Assert.Equal($@"{Ready}\{DelunoSide.FileNameOf(first.SourcePath())}", (string)resent["outputPath"]!);
        Assert.Single(world.Scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(Directory.GetFiles(world.Translation.Apply(Ready)));
        source.AssertStill();
    }

    // --- the status poll and the cancel of a hand-off waiting for its turn -----------------------------

    [Fact]
    public async Task A_hand_off_Weir_holds_queued_while_paused_reads_queued_in_the_position_Deluno_recorded_each_time_it_asks()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("status-ask-queued");
        // Deluno recorded it second in the queue: the hand-off Deluno stopped waiting for in the same session was queued ahead of it.
        await HandOffQueuedAsync(world, DelunoSide.Request("cancel-queued").Exchanges[1].Request.HandoffIdInPath(), "Gprecqueuedahead.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        var hand = await HandOffQueuedAsync(world, recording.Exchanges[0].Request.HandoffIdInPath(), "Gprecqueuedxrbw01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        Assert.Equal("queued", await StateOfAsync(world, hand.HandoffId()));
    }

    [Fact]
    public async Task A_queued_hand_off_whose_download_was_deleted_reads_queued_with_the_sentence_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("status-ask-queued-source-missing");
        var gone = new[] { "Gpgonewaitarzq01.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "Gpgonewaitablt02.2020.1080p.WEB-DL.x264-GOLDEN.mkv" };
        var ids = new List<string>();
        for (var index = 0; index < gone.Length; index++)
        {
            var hand = await HandOffQueuedAsync(world, recording.Exchanges[index].Request.HandoffIdInPath(), gone[index]);
            File.Delete(world.Translation.Apply(hand.SourcePath()));
            ids.Add(hand.HandoffId());
        }

        // Weir was paused when the downloads went; when it runs again it looks, finds them gone, and says so while it keeps them listed.
        await world.Scenario.SetPauseAsync(paused: false);
        foreach (var id in ids)
        {
            await Harness.Poll.UntilAsync(async () => ((string?)(await world.Scenario.HandoffStatusAsync(id))["message"])?.StartsWith("This file is no longer in the watched folder", StringComparison.Ordinal) == true ? "told" : null, $"Weir to say {id} is no longer in the watched folder");
        }

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);
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
        var single = DelunoSide.HandoffLike(recording.Exchanges[0].Request.HandoffIdInPath(), DelunoSide.FileNameOf((string)recording.Exchanges[0].Request.Body!["importedPath"]!));
        var folder = DelunoSide.RecordedHandoff(recording.Exchanges[1].Request.HandoffIdInPath())!;
        var singleFile = DelunoSide.FileNameOf(single.SourcePath());
        var release = (string)folder.Body!["releaseName"]!;
        await FinishedAsync(world, single, folder);
        // Deluno moved the copies into its library, so Weir's folder no longer holds them.
        File.Delete(world.Translation.Apply($@"{Ready}\{singleFile}"));
        File.Delete(world.Translation.Apply($@"{Ready}\{release}\{release}.mkv"));

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        var handback = await world.HandbackAsync(singleFile);
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        var inFolder = await world.HandbackAsync($"{release}/{release}.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)inFolder["outcome"]!, (string)inFolder["outcome_by"]!));
        Assert.Equal(
            [$"Deluno imported {release}.mkv", $"Deluno imported {singleFile}"],
            (await world.ActivityTitlesAsync("processing.handback_outcome")).Order());
    }

    [Fact]
    public async Task Deluno_saying_it_will_not_import_a_hand_off_it_removed_is_recorded_and_Weir_keeps_its_copy()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("outcome-not-imported-removed");
        var first = DelunoSide.RecordedHandoff(recording.Exchanges[0].Request.HandoffIdInPath())!;
        var other = DelunoSide.HandoffLike(recording.Exchanges[1].Request.HandoffIdInPath(), ReleaseRemoved(recording.Exchanges[1]));
        await FinishedAsync(world, first, other);

        await world.Replay.SendAsync(recording, 0);
        await world.Replay.SendAsync(recording, 1);

        foreach (var file in new[] { DelunoSide.FileNameOf(first.SourcePath()), DelunoSide.FileNameOf(other.SourcePath()) })
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
        var sends = DelunoSide.Request("handoff-resend-during");
        var removed = DelunoSide.Request("outcome-not-imported-removed");
        var imported = DelunoSide.Request("outcome-imported-for-replaced-send");
        var file = DelunoSide.FileNameOf(sends.Exchanges[0].Request.SourcePath());
        world.PutFilm(sends.Exchanges[0].Request.SourcePath(), needsWork: true);
        await world.Replay.SendAsync(sends, 0);
        await world.Scenario.WaitForHandoffStateAsync(sends.Exchanges[0].Request.HandoffId(), "completed");
        await world.Replay.SendAsync(removed, 0);
        await world.Replay.SendAsync(sends, 1);
        await world.Scenario.WaitForHandoffStateAsync(sends.Exchanges[1].Request.HandoffId(), "completed");
        // Deluno moved the copy it was given into its library.
        File.Delete(world.Translation.Apply($@"{Ready}\{file}"));

        await world.Replay.SendAsync(imported, 1);

        var handback = await world.HandbackAsync(file);
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.DoesNotContain("will not import", (string?)handback["release_note"] ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deluno_importing_the_release_it_sent_again_before_Weir_started_is_recorded_as_imported_for_the_new_send()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var sends = DelunoSide.Request("handoff-resend");
        var imported = DelunoSide.Request("outcome-imported-for-replaced-send");
        var file = DelunoSide.FileNameOf(sends.Exchanges[0].Request.SourcePath());
        world.PutFilm(sends.Exchanges[0].Request.SourcePath(), needsWork: true);
        await world.Scenario.SetPauseAsync(paused: true);
        await world.Replay.SendAsync(sends, 0);
        await world.Replay.SendAsync(sends, 1);
        await world.Scenario.SetPauseAsync(paused: false);
        await world.Scenario.WaitForHandoffStateAsync(sends.Exchanges[1].Request.HandoffId(), "completed");
        Assert.Equal("cancelled", await StateOfAsync(world, sends.Exchanges[0].Request.HandoffId()));
        File.Delete(world.Translation.Apply($@"{Ready}\{file}"));

        await world.Replay.SendAsync(imported, 0);

        var handback = await world.HandbackAsync(file);
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
    }

    [Fact]
    public async Task An_outcome_for_hand_offs_Weir_has_not_started_is_refused_409_in_the_words_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var recording = DelunoSide.Request("outcome-not-imported-removed-handoff-ended");
        await world.Scenario.SetPauseAsync(paused: true);
        foreach (var exchange in recording.Exchanges)
        {
            var hand = DelunoSide.RecordedHandoff(exchange.Request.HandoffIdInPath())
                ?? DelunoSide.HandoffLike(exchange.Request.HandoffIdInPath(), ReleaseRemoved(exchange));
            world.PutFilm(hand.SourcePath(), needsWork: true);
            var accepted = await world.Replay.SendCallAsync(hand);
            Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());
        }

        for (var index = 0; index < recording.Exchanges.Count; index++)
        {
            await world.Replay.SendAsync(recording, index);
        }

        Assert.Empty(await world.ActivityTitlesAsync("processing.handback_outcome"));
    }

    /// <summary>The release a "Deluno removed X from the download client" reason names, as a file name.</summary>
    private static string ReleaseRemoved(RecordedExchange outcome)
    {
        var reason = (string)outcome.Request.Body!["reason"]!;
        var name = reason["Deluno removed ".Length..reason.IndexOf(" from ", StringComparison.Ordinal)];
        return name.EndsWith(".mkv", StringComparison.Ordinal) ? name : name + ".mkv";
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
