using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Core.Tests.Processing;

/// <summary>The wording of each step of a file's story, for the record shapes the remux pass stores.</summary>
public sealed class FileStoryNarrationTests
{
    private static WireArray Items(params string[] values) => new(values.Select(value => (WireValue)new WireString(value)));

    private static WireObject Passed() => new WireObject().Set("ok", true);

    private static StoryStep Step(WireObject detail, string heading, string library = "") =>
        Assert.Single(FileStory.NarratePass(detail, library), step => step.Heading == heading);

    private static void NoStep(WireObject detail, string heading, string library = "") =>
        Assert.DoesNotContain(FileStory.NarratePass(detail, library), step => step.Heading == heading);

    [Theory]
    [InlineData("movie", "", "Weir took this file as a film.")]
    [InlineData("tv", "", "Weir took this file as a TV show.")]
    [InlineData("anime", "Films", "Weir took this file in the Films workflow.")]
    [InlineData("movie", "  Films  ", "Weir took this file as a film in the Films workflow.")]
    [InlineData("tv", "Shows", "Weir took this file as a TV show in the Shows workflow.")]
    public void The_pick_up_names_the_kind_of_file_and_the_workflow_when_it_knows_them(string scope, string library, string sentence) =>
        Assert.Equal(sentence, Step(Passed().Set("media_scope", scope), "Picked up", library).Sentence);

    [Fact]
    public void A_file_of_no_known_kind_in_no_workflow_has_no_pick_up()
    {
        NoStep(Passed().Set("media_scope", "anime"), "Picked up");
        NoStep(Passed(), "Picked up");
    }

    [Fact]
    public void Looking_inside_counts_the_tracks_and_lists_the_audio_and_subtitles()
    {
        var counts = new WireObject().Set("video", 1L).Set("audio", 2L).Set("subtitle", 0L);
        var detail = Passed().Set("stream_counts", counts).Set("audio_before", "English 5.1, French 2.0").Set("subs_before", "English");

        Assert.Equal(
            "It found 1 video track, 2 audio tracks and 0 subtitle tracks. Audio: English 5.1, French 2.0. Subtitles: English.",
            Step(detail, "Looked inside").Sentence);
    }

    [Fact]
    public void Track_counts_that_are_text_are_read_and_counts_that_are_nothing_are_zero()
    {
        var counts = new WireObject().Set("video", "1").Set("audio", "many");

        Assert.Equal("It found 1 video track, 0 audio tracks and 0 subtitle tracks.", Step(Passed().Set("stream_counts", counts), "Looked inside").Sentence);
    }

    [Fact]
    public void Without_counts_it_still_says_what_audio_or_subtitles_it_saw()
    {
        Assert.Equal("It looked inside the file. Audio: English.", Step(Passed().Set("audio_before", "English"), "Looked inside").Sentence);
        Assert.Equal("It looked inside the file. Subtitles: French.", Step(Passed().Set("subs_before", "French"), "Looked inside").Sentence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("—")]
    [InlineData("-")]
    [InlineData("none")]
    [InlineData("None")]
    public void A_track_line_that_says_there_are_none_is_left_out(string line)
    {
        NoStep(Passed().Set("audio_before", line).Set("subs_before", line), "Looked inside");
        NoStep(Passed().Set("audio_after", line).Set("subs_after", line), "Planned");
    }

    [Fact]
    public void A_file_passed_through_unchanged_says_so_before_anything_else_about_the_plan()
    {
        var detail = Passed().Set("pass_through_unchanged", true).Set("removed_audio", Items("commentary"));

        Assert.Equal(
            "You asked for this file to pass through unchanged, so Weir planned to hand it on exactly as it arrived.",
            Step(detail, "Planned").Sentence);
    }

    [Fact]
    public void A_plan_lists_each_kind_of_removal_in_turn()
    {
        var detail = Passed()
            .Set("removed_audio", Items("commentary", "descriptive"))
            .Set("removed_subtitles", Items("German"))
            .Set("removed_images", Items("cover.jpg"))
            .Set("removed_attachments", Items("font.ttf", "font2.ttf"))
            .Set("metadata_removed", true)
            .Set("audio_after", "English")
            .Set("subs_after", "English, French");

        Assert.Equal(
            "Weir planned to remove 2 audio tracks, remove 1 subtitle track, remove 1 embedded image, remove 2 attachments, strip the file's metadata. " +
            "Audio kept: English. Subtitles kept: English, French.",
            Step(detail, "Planned").Sentence);
    }

    [Fact]
    public void Empty_entries_in_a_removal_list_are_not_counted()
    {
        var detail = Passed().Set("removed_audio", new WireArray([new WireString("  "), WireNull.Instance, new WireString("commentary"), new WireInteger(7)]));

        Assert.Equal("Weir planned to remove 2 audio tracks.", Step(detail, "Planned").Sentence);
    }

    [Fact]
    public void A_removal_that_is_not_a_list_is_ignored()
    {
        NoStep(Passed().Set("removed_audio", "commentary"), "Planned");
    }

    [Fact]
    public void A_plan_with_no_removals_but_tracks_kept_names_the_tracks()
    {
        Assert.Equal(
            "Weir planned the output tracks. Audio kept: English.",
            Step(Passed().Set("audio_after", "English"), "Planned").Sentence);
        Assert.Equal(
            "Weir planned the output tracks. Subtitles kept: French.",
            Step(Passed().Set("subs_after", "French"), "Planned").Sentence);
    }

    [Fact]
    public void A_plan_with_nothing_to_say_is_left_out()
    {
        NoStep(Passed(), "Planned");
        NoStep(Passed().Set("remux_required", true), "Planned");
    }

    [Theory]
    [InlineData(new[] { "-c", "copy" }, true)]
    [InlineData(new[] { "-codec", "copy" }, true)]
    [InlineData(new[] { "-vcodec", "copy" }, true)]
    [InlineData(new[] { "-codec:v", "copy", "-c:a", "aac" }, true)]
    [InlineData(new[] { "-i", "in.mkv", "-c:v", "copy" }, true)]
    [InlineData(new[] { "-c:v", "libx264" }, false)]
    [InlineData(new[] { "-vcodec", "libx265", "-c", "copy" }, false)]
    [InlineData(new[] { "-i", "in.mkv", "-c:a", "copy" }, false)]
    [InlineData(new[] { "-c:v" }, false)]
    [InlineData(new string[0], false)]
    public void The_video_is_called_copied_only_when_the_command_copies_it(string[] argv, bool claimed)
    {
        var detail = Passed().Set("remux_required", true).Set("removed_audio", Items("commentary")).Set("ffmpeg_argv", Items(argv));

        Assert.Equal(claimed, Step(detail, "Planned").Sentence.Contains("not re-encoded", StringComparison.Ordinal));
    }

    [Fact]
    public void A_command_that_is_not_a_list_proves_nothing_and_a_failed_pass_claims_nothing()
    {
        var notAList = Passed().Set("remux_required", true).Set("removed_audio", Items("commentary")).Set("ffmpeg_argv", "-c copy");
        var failed = new WireObject()
            .Set("ok", false)
            .Set("remux_required", true)
            .Set("removed_audio", Items("commentary"))
            .Set("ffmpeg_argv", Items("-c", "copy"));

        Assert.DoesNotContain("not re-encoded", Step(notAList, "Planned").Sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("not re-encoded", Step(failed, "Planned").Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reasons_for_the_audio_choice_are_shown_four_at_most_each_ending_in_one_full_stop()
    {
        var detail = Passed().Set("audio_selection_notes", Items("Kept English.", "Kept the 5.1 mix", "Dropped commentary", "Dropped French", "Dropped German"));

        Assert.Equal(
            "Kept English. Kept the 5.1 mix. Dropped commentary. Dropped French.",
            Step(detail, "Why these tracks").Sentence);
        NoStep(Passed().Set("audio_selection_notes", new WireArray()), "Why these tracks");
    }

    [Theory]
    [InlineData(0.4, "It took under a second.")]
    [InlineData(1, "It took 1 second.")]
    [InlineData(45, "It took 45 seconds.")]
    [InlineData(89.4, "It took 89 seconds.")]
    [InlineData(90, "It took 2 minutes.")]
    [InlineData(600, "It took 10 minutes.")]
    [InlineData(5339, "It took 89 minutes.")]
    [InlineData(5400, "It took 1.5 hours.")]
    [InlineData(36000, "It took 10.0 hours.")]
    public void How_long_it_took_is_told_in_the_largest_fitting_unit(double seconds, string sentence) =>
        Assert.Equal(sentence, Step(Passed().Set("elapsed_seconds", seconds), "Worked").Sentence);

    [Fact]
    public void Elapsed_time_is_read_from_text_and_true_false_but_not_from_nonsense()
    {
        Assert.Equal("It took 30 seconds.", Step(Passed().Set("elapsed_seconds", "30"), "Worked").Sentence);
        Assert.Equal("It took 1 second.", Step(Passed().Set("elapsed_seconds", true), "Worked").Sentence);
        NoStep(Passed().Set("elapsed_seconds", false), "Worked");
        NoStep(Passed().Set("elapsed_seconds", double.NaN), "Worked");
        NoStep(Passed().Set("elapsed_seconds", new WireArray()), "Worked");
        NoStep(Passed().Set("elapsed_seconds", 0L), "Worked");
    }

    [Theory]
    [InlineData(2000, 1000, "2.0 KB became 1000 B, saving 1000 B.", StoryTone.Good)]
    [InlineData(5_242_880, 104_857_600, "5.0 MB became 100 MB.", StoryTone.Neutral)]
    [InlineData(157_286_400, 5_242_880, "150 MB became 5.0 MB, saving 145 MB.", StoryTone.Good)]
    [InlineData(5_000_000_000, 1_073_741_824, "4.7 GB became 1.0 GB, saving 3.7 GB.", StoryTone.Good)]
    [InlineData(2_199_023_255_552, 1_099_511_627_776, "2.0 TB became 1.0 TB, saving 1.0 TB.", StoryTone.Good)]
    [InlineData(219_902_325_555_200, 109_951_162_777_600, "200 TB became 100 TB, saving 100 TB.", StoryTone.Good)]
    [InlineData(1000, 1000, "The size stayed at 1000 B.", StoryTone.Neutral)]
    [InlineData(1000, 2000, "1000 B became 2.0 KB.", StoryTone.Neutral)]
    [InlineData(2048, -2048, "2.0 KB became -2.0 KB, saving 4.0 KB.", StoryTone.Good)]
    public void The_sizes_before_and_after_are_told_in_the_unit_that_fits_and_only_a_saving_is_good(double source, double output, string sentence, StoryTone tone)
    {
        var worked = Step(Passed().Set("source_size_bytes", source).Set("output_size_bytes", output), "Worked");

        Assert.Equal(sentence, worked.Sentence);
        Assert.Equal(tone, worked.Tone);
    }

    [Fact]
    public void Time_and_sizes_are_told_together()
    {
        var detail = Passed().Set("elapsed_seconds", 12.0).Set("source_size_bytes", 2000.0).Set("output_size_bytes", 1000.0);

        Assert.Equal("It took 12 seconds; 2.0 KB became 1000 B, saving 1000 B.", Step(detail, "Worked").Sentence);
    }

    [Fact]
    public void A_source_of_no_size_adds_nothing_and_a_missing_size_alone_is_not_enough()
    {
        NoStep(Passed().Set("source_size_bytes", 0L).Set("output_size_bytes", 0L), "Worked");
        NoStep(Passed().Set("source_size_bytes", 100L), "Worked");
        Assert.Equal("It took 3 seconds.", Step(Passed().Set("elapsed_seconds", 3L).Set("source_size_bytes", 0L).Set("output_size_bytes", 5L), "Worked").Sentence);
    }

    [Theory]
    [InlineData("passed", "Weir checked the finished file was complete before handing it on.", StoryTone.Good)]
    [InlineData("PASSED", "Weir checked the finished file was complete before handing it on.", StoryTone.Good)]
    [InlineData("failed", "The finished file did not pass Weir's completeness check.", StoryTone.Bad)]
    public void The_completeness_check_is_reported_either_way(string check, string sentence, StoryTone tone)
    {
        var step = Step(Passed().Set("output_completeness_check", check), "Checked");

        Assert.Equal(sentence, step.Sentence);
        Assert.Equal(tone, step.Tone);
    }

    [Fact]
    public void A_failed_check_gives_its_own_note_and_an_unknown_result_is_not_mentioned()
    {
        Assert.Equal(
            "The video ends 40 seconds early.",
            Step(Passed().Set("output_completeness_check", "failed").Set("output_completeness_note", "The video ends   40 seconds early."), "Checked").Sentence);
        NoStep(Passed().Set("output_completeness_check", "skipped"), "Checked");
        NoStep(Passed(), "Checked");
    }

    [Fact]
    public void An_output_that_already_existed_is_a_warning_only_when_the_file_was_skipped()
    {
        var skipped = Step(Passed().Set("output_collision_reason", "A cleaned copy was already there.").Set("output_collision_action", "Skip"), "An output already existed");
        var replaced = Step(Passed().Set("output_collision_reason", "A cleaned copy was already there.").Set("output_collision_action", "replace"), "An output already existed");

        Assert.Equal(StoryTone.Warn, skipped.Tone);
        Assert.Equal(StoryTone.Neutral, replaced.Tone);
        Assert.Equal("A cleaned copy was already there.", replaced.Sentence);
        NoStep(Passed().Set("output_collision_action", "skip"), "An output already existed");
    }

    [Fact]
    public void A_finished_file_with_no_destination_recorded_has_no_hand_back_step()
    {
        NoStep(Passed(), "Handed back");
        NoStep(Passed().Set("output_file", string.Empty), "Handed back");
    }

    [Fact]
    public void A_failure_falls_back_to_the_preflight_reason_when_it_has_no_reason_of_its_own()
    {
        var detail = new WireObject().Set("ok", false).Set("preflight_reason", "The file is not there.");

        Assert.Equal("The file is not there.", Step(detail, "Could not finish").Sentence);
    }

    [Fact]
    public void A_reason_is_shown_with_its_spaces_tidied_and_numbers_and_flags_in_their_plain_form()
    {
        Assert.Equal("Too many spaces here.", Step(new WireObject().Set("ok", false).Set("reason", "  Too   many\tspaces here. "), "Could not finish").Sentence);
        Assert.Equal("1.5", Step(new WireObject().Set("ok", false).Set("reason", 1.5), "Could not finish").Sentence);
        Assert.Equal("7", Step(new WireObject().Set("ok", false).Set("reason", 7L), "Could not finish").Sentence);
        Assert.Equal("True", Step(new WireObject().Set("ok", false).Set("reason", true), "Could not finish").Sentence);
        Assert.Equal("False", Step(new WireObject().Set("ok", false).Set("reason", false), "Could not finish").Sentence);
        Assert.Equal(
            "Weir could not process this file, and the record does not say why.",
            Step(new WireObject().Set("ok", false).Set("reason", new WireArray()), "Could not finish").Sentence);
    }

    [Fact]
    public void A_record_whose_ok_is_not_true_or_false_is_read_as_a_pass_that_worked()
    {
        var steps = FileStory.NarratePass(new WireObject().Set("ok", "no").Set("output_file", "/ready/Film.mkv"), "Films");

        Assert.Contains(steps, step => step.Heading == "Handed back");
        Assert.DoesNotContain(steps, step => step.Heading == "Could not finish");
    }

    [Fact]
    public void A_rejection_is_told_with_its_reason_tidied_into_a_sentence()
    {
        var withLead = new WireObject()
            .Set("ok", false)
            .Set(RejectionResultKeys.WithoutManager, true)
            .Set("reason", "Rejected: it has no English audio");
        var lowercase = new WireObject().Set("ok", false).Set(RejectionResultKeys.WithoutManager, true).Set("reason", "no English audio.");
        var leadOnly = new WireObject().Set("ok", false).Set(RejectionResultKeys.WithoutManager, true).Set("reason", "Rejected: ");

        Assert.Equal("It has no English audio.", Step(withLead, "Rejected").Sentence);
        Assert.Equal("No English audio.", Step(lowercase, "Rejected").Sentence);
        Assert.Equal("Weir rejected this file.", Step(leadOnly, "Rejected").Sentence);
    }

    [Fact]
    public void A_missing_story_is_empty()
    {
        Assert.Empty(FileStory.NarratePass(null));
    }
}
