using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Processing;

/// <summary>The last edges of how a pass is told and recorded: records with parts missing, titles with no summary, and workflows saved with odd values.</summary>
public sealed class ProcessingPassEdgeTests
{
    private static WireObject Obj(string json) => (WireObject)WireJsonParser.Parse(json);

    private static WireObject Passed() => new WireObject().Set("ok", true);

    private static StoryStep Step(WireObject detail, string heading) => Assert.Single(FileStory.NarratePass(detail, string.Empty), step => step.Heading == heading);

    [Fact]
    public void A_size_beyond_a_terabyte_is_shown_in_terabytes_with_its_sign()
    {
        var detail = Passed().Set("source_size_bytes", 100.0).Set("output_size_bytes", -2_000_000_000_000.0);

        Assert.Contains("became -1.8 TB", Step(detail, "Worked").Sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"audio": 2}""", "0 video tracks, 2 audio tracks and 0 subtitle tracks")]
    [InlineData("""{"video": 1, "subtitle": 3}""", "1 video track, 0 audio tracks and 3 subtitle tracks")]
    [InlineData("""{"video": "many"}""", "0 video tracks, 0 audio tracks and 0 subtitle tracks")]
    public void The_tracks_found_are_counted_whichever_kinds_the_record_names(string counts, string expected)
    {
        var detail = Passed().Set("stream_counts", Obj(counts));

        Assert.Contains(expected, Step(detail, "Looked inside").Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_counts_that_are_not_an_object_looks_inside_with_no_counts()
    {
        var detail = Passed().Set("stream_counts", "three").Set("audio_before", "English");

        Assert.Equal("It looked inside the file. Audio: English.", Step(detail, "Looked inside").Sentence);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"yes\"")]
    public void A_pass_through_flag_that_is_not_true_plans_nothing_special(string flag)
    {
        var detail = Passed().Set("pass_through_unchanged", Json(flag)).Set("removed_audio", new WireArray([new WireString("commentary")]));

        Assert.DoesNotContain("pass through unchanged", Step(detail, "Planned").Sentence, StringComparison.Ordinal);
    }

    private static WireValue Json(string json) => WireJsonParser.Parse(json);

    [Theory]
    [InlineData("skip", StoryTone.Warn)]
    [InlineData("replace", StoryTone.Neutral)]
    public void An_output_that_already_existed_is_a_warning_only_when_the_file_was_skipped(string action, StoryTone tone)
    {
        var detail = Passed().Set("output_collision_reason", "A file was already there.").Set("output_collision_action", action);

        Assert.Equal(tone, Step(detail, "An output already existed").Tone);
        Assert.Equal(StoryTone.Neutral, Step(Passed().Set("output_collision_reason", "x"), "An output already existed").Tone);
    }

    [Theory]
    [InlineData("""{"relative_media_path": "Film.mkv", "rejected_without_manager": true}""", "Rejected Film.mkv")]
    [InlineData("""{"relative_media_path": "Film.mkv", "rejected_without_manager": true, "rejection_summary": "no English audio"}""", "Rejected Film.mkv: no English audio")]
    [InlineData("""{"relative_media_path": "Film.mkv", "rejected_without_manager": true, "rejection_summary": ""}""", "Rejected Film.mkv")]
    public void A_rejection_is_titled_with_its_summary_when_there_is_one(string payload, string expected)
    {
        Assert.Equal(expected, RemuxPassVisibility.ActivityTitle(Obj(payload)));
    }

    [Theory]
    [InlineData("""{"ok": false}""", "failed", "worker")]
    [InlineData("""{"ok": false, "retry_scheduled": true, "trigger": "scheduled"}""", "retrying", "scheduled")]
    [InlineData("""{"ok": true, "trigger": 5}""", "success", "worker")]
    [InlineData("""{"ok": true, "trigger": "nonsense"}""", "success", "worker")]
    [InlineData("""{"outcome": "source_gone"}""", "skipped", "worker")]
    public void A_pass_is_clipped_for_activity_with_the_result_and_trigger_it_names(string payload, string result, string trigger)
    {
        var clipped = RemuxPassVisibility.ClipForActivity(Obj(payload));

        Assert.Equal(result, ((WireString)clipped.Get("result")!).Value);
        Assert.Equal(trigger, ((WireString)clipped.Get("trigger")!).Value);
    }

    [Theory]
    [InlineData("""{"index": 1, "codec_type": "audio", "disposition": {"default": 1, "forced": 0}}""", true, false)]
    [InlineData("""{"index": 1, "codec_type": "audio", "disposition": 3}""", false, false)]
    [InlineData("""{"index": 1, "codec_type": "audio"}""", false, false)]
    public void A_pass_through_plan_reads_the_flags_a_stream_carries(string audio, bool isDefault, bool forced)
    {
        var plan = RemuxPassMedia.PassThroughPlan([], [ProbeStreamInfo.Parse(audio)], []);

        Assert.Equal((isDefault, forced), (plan.Audio[0].Default, plan.Audio[0].Forced));
    }

    [Theory]
    [InlineData("""{"codec_name": "mjpeg", "nb_frames": "1"}""", true)]
    [InlineData("""{"codec_name": "mjpeg", "nb_frames": "5", "avg_frame_rate": "0/0"}""", true)]
    [InlineData("""{"codec_name": "mjpeg", "nb_frames": "5", "avg_frame_rate": "25/1"}""", false)]
    [InlineData("""{"codec_name": "mjpeg", "nb_frames": null, "avg_frame_rate": "25/1"}""", false)]
    public void A_picture_stream_is_a_still_by_its_frame_count_and_rate(string stream, bool image)
    {
        Assert.Equal(image, MetadataStreams.IsImageStream(ProbeStreamInfo.Parse(stream)));
    }

    [Fact]
    public void A_dub_named_track_is_noted_as_such_when_it_is_kept()
    {
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality };
        var video = ProbeStreamInfo.Parse("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");
        var dub = ProbeStreamInfo.Parse("""{"index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng", "title": "English Dub"}}""");

        var plan = RemuxRules.PlanRemux([video], [dub], [], config)!;

        Assert.Contains(plan.AudioSelectionNotes, note => note.Contains("dub track from its name", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_audio_track_kept_leaves_nothing_to_report_as_removed_whichever_way_audio_is_kept()
    {
        var video = ProbeStreamInfo.Parse("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");
        var english = ProbeStreamInfo.Parse("""{"index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng"}}""");
        var french = ProbeStreamInfo.Parse("""{"index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "fre"}}""");
        var perLanguage = RemuxRules.DefaultConfig() with { AudioKeepMode = RemuxRuleValues.AudioKeepModePerLanguage, PrimaryAudioLang = "eng", SecondaryAudioLang = "fre" };

        Assert.Empty(RemuxRules.PlanRemux([video], [english, french], [], perLanguage)!.RemovedAudio);
        Assert.Empty(RemuxRules.PlanRemux([video], [english], [], RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng" })!.RemovedAudio);
    }

    [Fact]
    public void A_workflow_saved_with_a_schedule_that_cannot_be_read_is_refused_in_plain_words()
    {
        var input = new ProcessingLibraryInput { Name = "Movies", MediaType = "movie", ScheduleGrid = "not a grid" };

        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ApplyFields(new ProcessingLibraryRecord { Name = "Movies" }, input, ruleSetExists: true));
    }

    [Fact]
    public void A_workflow_row_with_missing_lists_has_no_extensions_markers_or_patterns()
    {
        var rules = LibraryAdmissionRules.For(new ProcessingLibraryRecord
        {
            Name = "Movies",
            MediaExtensionsCsv = null!,
            ExcludeMarkersCsv = null!,
            IncludePatternsCsv = null!,
            ExcludePatternsCsv = null!,
            RejectedFileAction = null!,
        });

        Assert.Empty(rules.MediaExtensions);
        Assert.Empty(rules.IncludePatterns);
        Assert.Equal("leave", rules.RejectedFileAction);
        Assert.Equal("delete_file", LibraryAdmissionRules.For(new ProcessingLibraryRecord { Name = "Movies", RejectedFileAction = " DELETE_FILE " }).RejectedFileAction);
    }

    [Fact]
    public void A_gate_compares_a_release_with_the_managers_library_case_sensitively_unless_it_is_told_not_to()
    {
        var main = new ManagerConnection("deluno", "Main", "http://m", "k");
        var answers = new[] { new ManagerLibraryTruth(main, SignalStatus.Reported, ["/other/Film/FILM.mkv"]) };

        var strict = LibraryTruthGate.EvaluateForFolder(answers, "/lib/Film", "movie", path => path, false, "/other/Film/film.mkv");
        var folded = LibraryTruthGate.EvaluateForFolder(answers, "/lib/Film", "movie", path => path, true, "/other/Film/film.mkv");
        var untitled = LibraryTruthGate.EvaluateForFolder([new ManagerLibraryTruth(main, SignalStatus.Reported, ["/other/x.mkv"])], "/lib/Film", "movie", path => path, false, "/lib/.mkv");

        Assert.Equal(LibraryTruthVerdict.Passed, strict.Check);
        Assert.Equal(LibraryTruthVerdict.Passed, folded.Check);
        Assert.Equal(LibraryTruthVerdict.Skipped, untitled.Check);
    }

    [Fact]
    public void A_workflow_row_with_every_date_limit_set_carries_them_into_its_rules()
    {
        var at = Weir.Core.Time.Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var rules = LibraryAdmissionRules.For(new ProcessingLibraryRecord { Name = "Movies", CreatedAfter = at, CreatedBefore = at, ModifiedAfter = at, ModifiedBefore = at });

        Assert.Equal([at.AsUtc, at.AsUtc, at.AsUtc, at.AsUtc], [rules.CreatedAfter, rules.CreatedBefore, rules.ModifiedAfter, rules.ModifiedBefore]);
    }

    [Theory]
    [InlineData("""{"relative_media_path": "Film.mkv", "rejected_without_manager": true, "rejection_summary": 5}""", "Rejected Film.mkv")]
    public void A_rejection_summary_that_is_not_text_is_left_out_of_the_title(string payload, string expected)
    {
        Assert.Equal(expected, RemuxPassVisibility.ActivityTitle(Obj(payload)));
    }

    [Theory]
    [InlineData("""{"ok": true, "outcome": ""}""", "success")]
    [InlineData("""{"ok": true, "outcome": null}""", "success")]
    [InlineData("""{"ok": true, "outcome": "live_output_written"}""", "success")]
    public void A_pass_whose_outcome_is_empty_or_null_is_clipped_by_whether_it_worked(string payload, string result)
    {
        Assert.Equal(result, ((WireString)RemuxPassVisibility.ClipForActivity(Obj(payload)).Get("result")!).Value);
    }

    [Fact]
    public void A_still_picture_whose_frame_count_cannot_be_read_is_told_by_its_frame_rate()
    {
        Assert.True(MetadataStreams.IsImageStream(ProbeStreamInfo.Parse("""{"codec_name": "png", "nb_frames": "n/a", "avg_frame_rate": "0/0"}""")));
    }

    [Theory]
    [InlineData("fre", "VFQ", "from the track name 'VFQ'")]
    [InlineData("fre-CA", "", "from the language tag 'fre-CA'")]
    public void A_regional_variant_kept_is_noted_with_where_it_was_found(string language, string title, string expected)
    {
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "fre", AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality };
        var video = ProbeStreamInfo.Parse("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");
        var audio = ProbeStreamInfo.Parse(
            "{\"index\": 1, \"codec_type\": \"audio\", \"codec_name\": \"aac\", \"channels\": 2, \"tags\": {\"language\": \"" + language + "\", \"title\": \"" + title + "\"}}");

        var plan = RemuxRules.PlanRemux([video], [audio], [], config)!;

        Assert.Contains(plan.AudioSelectionNotes, note => note.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_windows_folders_a_workflow_may_not_use_are_the_ones_the_host_has_and_none_for_one_that_has_not()
    {
        var host = LibraryRules.WindowsReservedRoots(folder => folder == Environment.SpecialFolder.Windows ? @"C:\Windows" : @"C:\Program Files").ToList();
        var other = LibraryRules.WindowsReservedRoots(_ => string.Empty).ToList();

        Assert.Equal([@"C:\Windows", @"C:\Program Files", @"C:\Program Files"], host);
        Assert.Empty(other);
        Assert.Equal(OperatingSystem.IsWindows(), LibraryRules.WindowsReservedRoots(Environment.GetFolderPath).Any());
    }
}
