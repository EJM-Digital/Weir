using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Processing.RemuxPass;

/// <summary>What a remux pass shows about its plan and outcome.</summary>
public sealed class RemuxPassVisibilityTests
{
    private static WireObject Parse(string json) => (WireObject)WireJsonParser.Parse(json);

    [Fact]
    public void The_plan_summary_names_the_streams()
    {
        var plan = new RemuxPlan
        {
            VideoIndices = [0],
            Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Channels = 2, CodecName = "aac" }],
            Subtitles = [],
            RemovedAudio = ["#2 commentary"],
        };

        var summary = RemuxPassVisibility.SummarizeRemuxPlan(plan);

        Assert.Contains("video copy indices: [0]", summary, StringComparison.Ordinal);
        Assert.Contains("#1 eng", summary, StringComparison.Ordinal);
        Assert.Contains("commentary", summary, StringComparison.Ordinal);
        Assert.Equal("video copy indices: [0] | audio out: #1 eng | subtitles out: none | removed audio: #2 commentary", summary);
    }

    [Fact]
    public void A_long_plan_summary_is_truncated_and_says_so()
    {
        var plan = new RemuxPlan
        {
            VideoIndices = [0],
            Audio = [],
            Subtitles = [],
            RemovedAudio = [.. Enumerable.Range(0, 8).Select(i => new string('x', 120) + i)],
        };

        var summary = RemuxPassVisibility.SummarizeRemuxPlan(plan);

        Assert.EndsWith("…(truncated)", summary, StringComparison.Ordinal);
        Assert.Equal(600, summary.Length);
        Assert.Contains("(+2 more)", RemuxPassVisibility.SummarizeRemuxPlan(plan, 5000), StringComparison.Ordinal);
    }

    [Fact]
    public void The_activity_title_follows_the_outcome()
    {
        WireObject Payload(string outcome, bool? ok = null, bool passThrough = false)
        {
            var payload = new WireObject().Set("relative_media_path", "movies/foo.mkv").Set("outcome", outcome);
            if (ok is { } value)
            {
                payload.Set("ok", value);
            }

            if (passThrough)
            {
                payload.Set("pass_through_unchanged", true);
            }

            return payload;
        }

        Assert.Equal("foo.mkv was processed successfully", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.LiveOutputWritten)));
        Assert.Equal("Waiting for foo.mkv", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.SourceNotReady, ok: false)));
        Assert.Equal("No changes needed for foo.mkv", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.LiveSkippedNotRequired)));
        Assert.Equal("foo.mkv was passed through unchanged", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.LiveSkippedNotRequired, passThrough: true)));
        Assert.Equal("Skipped foo.mkv", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.SkippedGuardrail)));
        Assert.Equal("foo.mkv could not be processed", RemuxPassVisibility.ActivityTitle(Payload(RemuxPassOutcomes.FailedDuringExecution)));
        Assert.Equal("foo.mkv could not be checked", RemuxPassVisibility.ActivityTitle(Payload("anything", ok: false)));
        Assert.Equal("File processing finished", RemuxPassVisibility.ActivityTitle(new WireObject()));
        Assert.Equal("unknown file could not be checked", RemuxPassVisibility.ActivityTitle(new WireObject().Set("ok", false)));
    }

    [Fact]
    public void Source_not_ready_is_an_expected_wait_not_a_failure()
    {
        var detail = Parse(RemuxPassVisibility.ActivityDetail(new WireObject()
            .Set("ok", false)
            .Set("outcome", RemuxPassOutcomes.SourceNotReady)
            .Set("retryable_wait", true)
            .Set("relative_media_path", "movies/downloading.mkv")
            .Set("reason", "This file is still open for writing.")));

        Assert.Equal("skipped", WireConvert.Str(detail["result"]));
        Assert.Equal("info", WireConvert.Str(detail["severity"]));
        Assert.False(detail.ContainsKey("next_action"));
    }

    [Fact]
    public void A_failure_with_a_retry_coming_is_retrying_and_a_queued_follow_up_needs_no_action()
    {
        var retrying = RemuxPassVisibility.ClipForActivity(new WireObject().Set("ok", false).Set("outcome", RemuxPassOutcomes.FailedDuringExecution).Set("retry_scheduled", true));
        Assert.Equal("retrying", WireConvert.Str(retrying["result"]));

        var failed = RemuxPassVisibility.ClipForActivity(new WireObject().Set("ok", false).Set("outcome", RemuxPassOutcomes.FailedDuringExecution));
        Assert.Equal("failed", WireConvert.Str(failed["result"]));
        Assert.Contains("use Try again there", WireConvert.Str(failed["next_action"]), StringComparison.Ordinal);

        var handedBack = RemuxPassVisibility.ClipForActivity(new WireObject().Set("ok", false).Set("outcome", RemuxPassOutcomes.FailedDuringExecution).Set("pass_through_queued", true));
        Assert.False(handedBack.ContainsKey("next_action"));
    }

    [Fact]
    public void Long_argv_lists_are_clipped_for_activity()
    {
        var argv = new WireArray(Enumerable.Range(0, 100).Select(i => (WireValue)new WireString($"a{i}")));

        var clipped = RemuxPassVisibility.ClipForActivity(new WireObject().Set("ffmpeg_argv", argv));

        Assert.Equal(WireBool.True, clipped["ffmpeg_argv_truncated"]);
        Assert.Equal(65, ((WireArray)clipped["ffmpeg_argv"]).Items.Count);
        Assert.Equal(100, argv.Items.Count);
    }

    [Fact]
    public void The_detail_is_compact_ascii_json_with_removed_tracks_and_counts()
    {
        var detail = RemuxPassVisibility.ActivityDetail(new WireObject()
            .Set("ok", true)
            .Set("outcome", RemuxPassOutcomes.LiveOutputWritten)
            .Set("relative_media_path", "movies/sample.mkv")
            .Set("removed_audio", new WireArray([new WireString("eng commentary"), new WireString("jpn stereo")]))
            .Set("removed_subtitles", new WireArray([new WireString("spa"), new WireString("fre")])));

        Assert.Contains("\"outcome\":\"live_output_written\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"removed_audio\":[\"eng commentary\",\"jpn stereo\"]", detail, StringComparison.Ordinal);
        Assert.Contains("\"removed_subtitles\":[\"spa\",\"fre\"]", detail, StringComparison.Ordinal);
        Assert.Contains("\"audio_removed\":2", detail, StringComparison.Ordinal);
        Assert.Contains("\"subtitles_removed\":2", detail, StringComparison.Ordinal);
        Assert.Contains("\"module\":\"processing\",\"action\":\"remux\",\"trigger\":\"worker\",\"result\":\"success\"", detail, StringComparison.Ordinal);
        Assert.Equal(
            "\"relative_media_path\":\"x\\u00e9.mkv\"",
            RemuxPassVisibility.ActivityDetail(new WireObject().Set("relative_media_path", "xé.mkv")).Split(',')[0].TrimStart('{'));
    }
}

/// <summary>Failure classes and the requeue policy each library's retry settings give them.</summary>
public sealed class FailureClassesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ProcessingLibraryRecord Library(long maxAttempts = 3, long backoff = 300, bool retryExecution = true, bool retryPreflight = false) => new()
    {
        Name = "Movies",
        MaxAttempts = maxAttempts,
        RetryBackoffSeconds = backoff,
        RetryExecutionFailures = retryExecution,
        RetryPreflightFailures = retryPreflight,
    };

    [Theory]
    [InlineData("failed_before_execution", "preflight")]
    [InlineData("failed_during_execution", "execution")]
    [InlineData("skipped_guardrail", "guardrail")]
    [InlineData(" FAILED_DURING_EXECUTION ", "execution")]
    [InlineData("source_not_ready", "unknown")]
    [InlineData(null, "unknown")]
    public void Outcomes_map_onto_the_retry_vocabulary(string? outcome, string expected) =>
        Assert.Equal(expected, ProcessingFailureClasses.Classify(outcome));

    [Fact]
    public void Guardrail_and_unknown_are_never_retryable()
    {
        Assert.False(ProcessingFailureClasses.IsRetryable("guardrail", true, true));
        Assert.False(ProcessingFailureClasses.IsRetryable("unknown", true, true));
        Assert.True(ProcessingFailureClasses.IsRetryable("execution", false, true));
        Assert.False(ProcessingFailureClasses.IsRetryable("preflight", false, true));
    }

    [Fact]
    public void Backoff_doubles_from_the_base_and_caps_at_an_hour()
    {
        Assert.Equal(300, ProcessingFailureClasses.BackoffSecondsForAttempt(1, 300));
        Assert.Equal(600, ProcessingFailureClasses.BackoffSecondsForAttempt(2, 300));
        Assert.Equal(3600, ProcessingFailureClasses.BackoffSecondsForAttempt(5, 300));
        Assert.Equal(3600, ProcessingFailureClasses.BackoffSecondsForAttempt(40, 1));
        Assert.Equal(1, ProcessingFailureClasses.BackoffSecondsForAttempt(0, 0));
    }

    [Fact]
    public void A_first_execution_failure_is_retried_after_the_base_delay()
    {
        var decision = RetryPolicy.DecideRetry(Library(), "execution", 1, Now);

        Assert.True(decision.WillRetry);
        Assert.Equal(Now.AddSeconds(300), decision.NextRetryAt);
        Assert.Equal("This failed and Weir will try again in about 5 minutes (attempt 2 of 3).", decision.Reason);
    }

    [Fact]
    public void A_one_minute_retry_reads_in_the_singular()
    {
        var decision = RetryPolicy.DecideRetry(Library(backoff: 60), "execution", 1, Now);

        Assert.Equal("This failed and Weir will try again in about 1 minute (attempt 2 of 3).", decision.Reason);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 3, false)]
    [InlineData(5, 4, true)]
    [InlineData(5, 5, false)]
    [InlineData(10, 9, true)]
    [InlineData(10, 10, false)]
    public void The_workflows_attempt_limit_allows_exactly_that_many_attempts(long maxAttempts, long attemptsMade, bool willRetry)
    {
        var decision = RetryPolicy.DecideRetry(Library(maxAttempts: maxAttempts), "execution", attemptsMade, Now);

        Assert.Equal(willRetry, decision.WillRetry);
    }

    [Fact]
    public void Running_out_of_attempts_says_how_many_were_made_and_what_the_workflow_allows()
    {
        var decision = RetryPolicy.DecideRetry(Library(maxAttempts: 5), "execution", 5, Now);

        Assert.Null(decision.NextRetryAt);
        Assert.Equal(
            "Weir tried this file 5 times and stopped, because the Movies workflow allows 5. You can still start it again by hand.",
            decision.Reason);
    }

    [Fact]
    public void A_workflow_that_allows_one_attempt_says_so_in_the_singular()
    {
        var decision = RetryPolicy.DecideRetry(Library(maxAttempts: 1), "execution", 1, Now);

        Assert.Equal(
            "Weir tried this file 1 time and stopped, because the Movies workflow allows 1. You can still start it again by hand.",
            decision.Reason);
    }

    [Fact]
    public void Repeated_failures_of_one_class_are_retried_up_to_the_workflows_limit_and_never_held_earlier()
    {
        var decision = RetryPolicy.DecideRetry(Library(maxAttempts: 10), "execution", 3, Now);

        Assert.True(decision.WillRetry);
        Assert.Equal(Now.AddSeconds(1200), decision.NextRetryAt);
    }

    [Fact]
    public void A_preflight_failure_is_not_retried_by_default()
    {
        var decision = RetryPolicy.DecideRetry(Library(), "preflight", 1, Now);

        Assert.False(decision.WillRetry);
        Assert.StartsWith("This file was rejected before any work started", decision.Reason, StringComparison.Ordinal);
    }
}

/// <summary>Size settling: a file is left alone until its size stops changing.</summary>
public sealed class FileSettlingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ProcessingLibraryRecord Library(long interval = 30, bool ignore = false) =>
        new() { Name = "Movies", ReadyAfterSeconds = interval, IgnoreSizeChanges = ignore };

    [Fact]
    public void A_file_seen_for_the_first_time_is_treated_as_still_settling()
    {
        var observation = FileSettling.ObserveSizeSettling(Library(), null, null, 1_000, Now);

        Assert.True(observation.IsSettling);
        Assert.Equal(Now.AddSeconds(30), observation.StableAt);
        Assert.Contains("only just found", observation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_growing_file_stays_settling_and_restarts_the_clock()
    {
        var observation = FileSettling.ObserveSizeSettling(Library(), 1_000, Now.AddMinutes(-5), 2_000, Now);

        Assert.True(observation.IsSettling);
        Assert.Equal(Now, observation.SizeChangedAt);
        Assert.Equal(Now.AddSeconds(30), observation.StableAt);
        Assert.Contains("still growing", observation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_size_that_has_held_still_long_enough_is_settled()
    {
        var observation = FileSettling.ObserveSizeSettling(Library(), 2_000, Now.AddSeconds(-31), 2_000, Now);

        Assert.False(observation.IsSettling);
        Assert.Null(observation.Reason);
    }

    [Fact]
    public void A_size_that_has_only_just_stopped_is_not_settled_yet()
    {
        var observation = FileSettling.ObserveSizeSettling(Library(), 2_000, Now.AddSeconds(-5), 2_000, Now);

        Assert.True(observation.IsSettling);
        Assert.Equal(Now.AddSeconds(25), observation.StableAt);
        Assert.Contains("Weir waits 30s", observation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stalled_download_that_resumes_starts_settling_again()
    {
        var library = Library();
        Assert.False(FileSettling.ObserveSizeSettling(library, 2_000, Now.AddHours(-1), 2_000, Now).IsSettling);

        var resumed = FileSettling.ObserveSizeSettling(library, 2_000, Now.AddHours(-1), 2_500, Now);
        Assert.True(resumed.IsSettling);
        Assert.Equal(Now, resumed.SizeChangedAt);

        Assert.True(FileSettling.ObserveSizeSettling(library, 2_500, Now, 2_500, Now.AddSeconds(10)).IsSettling);
    }

    [Fact]
    public void A_same_size_file_with_no_recorded_change_moment_is_not_assumed_settled()
    {
        var observation = FileSettling.ObserveSizeSettling(Library(), 2_000, null, 2_000, Now);

        Assert.True(observation.IsSettling);
        Assert.Equal(Now, observation.SizeChangedAt);
    }

    [Fact]
    public void Ignoring_size_changes_or_a_zero_interval_opts_a_library_out()
    {
        Assert.False(FileSettling.ObserveSizeSettling(Library(ignore: true), 1_000, Now, 9_999, Now).IsSettling);
        Assert.False(FileSettling.ObserveSizeSettling(Library(interval: 0), null, null, 1, Now).IsSettling);
    }
}

/// <summary>The library-truth gate: what the media managers report about a file's folder.</summary>
public sealed class LibraryTruthGateTests
{
    private static readonly string Folder = OperatingSystem.IsWindows() ? @"C:\out\Title" : "/out/Title";

    private static ManagerLibraryTruth Reported(IReadOnlyList<string> paths, string kind = "radarr", string name = "Main") =>
        new(new ManagerConnection(kind, name, "http://x", "k"), SignalStatus.Reported, paths);

    private static string? Resolve(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Join(OperatingSystem.IsWindows() ? @"C:\cwd" : "/cwd", raw));
    }

    private static LibraryTruthVerdict Evaluate(IReadOnlyList<ManagerLibraryTruth> answers, string scope = "movie") =>
        LibraryTruthGate.EvaluateForFolder(answers, Folder, scope, Resolve, OperatingSystem.IsWindows());

    [Fact]
    public void No_manager_connected_never_clears_a_delete()
    {
        var verdict = Evaluate([]);

        Assert.Equal("skipped", verdict.Check);
        Assert.False(verdict.ClearsDelete);
        Assert.Contains("No media manager is connected for Movies", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_manager_reporting_nothing_inside_the_folder_clears_it()
    {
        var verdict = Evaluate([Reported([Path.Join(Folder, "..", "elsewhere", "f.mkv")], name: "1080p"), Reported([], kind: "sonarr")]);

        Assert.Equal("passed", verdict.Check);
        Assert.True(verdict.ClearsDelete);
        Assert.Contains("Radarr (1080p), Sonarr (Main)", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void One_manager_keeping_a_file_inside_the_folder_blocks_the_delete()
    {
        var kept = Path.Join(Folder, "f.mkv");
        var verdict = Evaluate([Reported([], name: "1080p"), Reported([kept], name: "4K")]);

        Assert.Equal("failed", verdict.Check);
        Assert.Contains("Radarr (4K) still keeps at least one library file", verdict.Note, StringComparison.Ordinal);
        Assert.EndsWith($"will not delete it. Example path: {Path.GetFullPath(kept)}", verdict.Note, StringComparison.Ordinal);
        Assert.Equal([Path.GetFullPath(kept)], verdict.MatchedPaths);
    }

    [Fact]
    public void Several_kept_files_are_named_as_example_paths()
    {
        var first = Path.Join(Folder, "a.mkv");
        var second = Path.Join(Folder, "b.mkv");
        var verdict = Evaluate([Reported([first, second], name: "4K")]);

        Assert.Equal("failed", verdict.Check);
        Assert.EndsWith(
            $"will not delete it. Example paths: {Path.GetFullPath(first)}; {Path.GetFullPath(second)}",
            verdict.Note,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreachable_or_silent_manager_blocks_the_delete_and_is_named()
    {
        var unreachable = new ManagerLibraryTruth(new ManagerConnection("radarr", "4K", "http://x", "k"), SignalStatus.Unreachable, [], "Weir could not reach Radarr (4K).");
        var verdict = Evaluate([Reported([], name: "1080p"), unreachable]);
        Assert.Equal("skipped", verdict.Check);
        Assert.Contains("could not confirm with Radarr (4K)", verdict.Note, StringComparison.Ordinal);
        Assert.EndsWith("Weir could not reach Radarr (4K).", verdict.Note, StringComparison.Ordinal);

        var silent = new ManagerLibraryTruth(new ManagerConnection("deluno", "Main", "http://x", "k"), SignalStatus.NoSignal, []);
        Assert.Contains("could not confirm with Deluno (Main)", Evaluate([Reported([], name: "1080p"), silent]).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_path_is_ignored_rather_than_crashing_the_gate() =>
        Assert.Equal("passed", Evaluate([Reported(["", "   ", "relative/but/elsewhere.mkv"])], "tv").Check);
}

/// <summary>#537 item 4: the title a lookup asks about.</summary>
public sealed class ReleaseTitleTests
{
    [Theory]
    [InlineData("The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", "the terror", 1963)]
    [InlineData("Blade Runner 2049 (2017)", "blade runner 2049", 2017)]
    [InlineData("Amelie.2001.REPACK.1080p.BluRay.x264", "amelie", 2001)]
    [InlineData("Some Film", "some film", null)]
    [InlineData("Some.Film.1080p.x264", "some film", null)]
    public void The_title_is_the_words_before_the_year(string name, string title, int? year) =>
        Assert.Equal((title, year), ReleaseTitle.Parse(name));

    [Theory]
    [InlineData("")]
    [InlineData("1080p.x264")]
    [InlineData(null)]
    public void Nothing_usable_is_null(string? name) => Assert.Null(ReleaseTitle.Parse(name));
}

/// <summary>What a pass reads off the probe.</summary>
public sealed class RemuxPassMediaTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    [Fact]
    public void Dimensions_come_from_the_largest_video_stream()
    {
        var (width, height) = RemuxPassMedia.VideoDimensions([
            Stream("""{"index":0,"width":300,"height":450}"""),
            Stream("""{"index":1,"coded_width":"1920","height":"N/A","coded_height":1080}"""),
        ]);

        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
        Assert.Equal((null, null), RemuxPassMedia.VideoDimensions([Stream("""{"index":0}""")]));
    }

    [Fact]
    public void Duration_is_the_longest_positive_value()
    {
        var probe = ProbeResult.Parse("""{"format":{"duration":"100.5"},"streams":[{"duration":"N/A"},{"duration":120.25},{"duration":null}]}""");

        Assert.Equal(120.25, RemuxPassMedia.ProbeDurationSeconds(probe));
        Assert.Null(RemuxPassMedia.ProbeDurationSeconds(ProbeResult.Parse("""{"streams":[]}""")));
    }

    [Theory]
    [InlineData("""{"bits_per_raw_sample":"10"}""", 10L)]
    [InlineData("""{"bits_per_raw_sample":"N/A","pix_fmt":"yuv420p10le"}""", 10L)]
    [InlineData("""{"pix_fmt":"yuv420p12"}""", 12L)]
    [InlineData("""{"pix_fmt":"yuv420p"}""", 8L)]
    [InlineData("""{}""", null)]
    public void Bit_depth_reads_the_raw_sample_count_then_the_pixel_format(string json, long? expected) =>
        Assert.Equal(expected, RemuxPassMedia.VideoBitDepth(Stream(json)));

    [Fact]
    public void A_pass_through_plan_keeps_every_stream_as_it_is()
    {
        var plan = RemuxPassMedia.PassThroughPlan(
            [Stream("""{"index":0,"codec_type":"video"}""")],
            [Stream("""{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"bit_rate":"N/A","tags":{"language":"fra"},"disposition":{"default":1}}""")],
            [Stream("""{"index":2,"codec_type":"subtitle","disposition":{"forced":1}}""")]);

        Assert.Equal([0], plan.VideoIndices);
        var audio = Assert.Single(plan.Audio);
        Assert.Equal(("fra", true, 2, 0L, "aac"), (audio.LangLabel, audio.Default, audio.Channels, audio.Bitrate, audio.CodecName));
        var subtitle = Assert.Single(plan.Subtitles);
        Assert.Equal(("und", true, TrackKind.Subtitle), (subtitle.LangLabel, subtitle.Forced, subtitle.Kind));
        Assert.Equal(["The operator chose Pass through unchanged, so every stream was preserved."], plan.AudioSelectionNotes);
    }
}
