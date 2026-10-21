using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Processing.RemuxPass;

/// <summary>How a pass reads in the activity list: its plan summary, its title, and what is clipped from its payload.</summary>
public sealed class RemuxPassVisibilityEdgeTests
{
    private static WireObject Payload(string json) => (WireObject)WireJsonParser.Parse(json);

    [Fact]
    public void A_plan_with_no_subtitles_says_none_and_lists_what_was_removed_from_them()
    {
        var plan = new RemuxPlan
        {
            VideoIndices = [0],
            Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Kind = TrackKind.Audio }],
            Subtitles = [],
            RemovedSubtitles = ["fre", "spa", "ger", "ita", "por", "dut", "swe", "nor"],
        };

        Assert.Equal(
            "video copy indices: [0] | audio out: #1 eng | subtitles out: none | removed subs: fre; spa; ger; ita; por; dut (+2 more)",
            RemuxPassVisibility.SummarizeRemuxPlan(plan));
    }

    [Fact]
    public void A_plan_with_nothing_kept_says_none_for_audio_too()
    {
        var plan = new RemuxPlan { VideoIndices = [], Audio = [], Subtitles = [new PlannedTrack { InputIndex = 2, LangLabel = string.Empty, Kind = TrackKind.Subtitle }] };

        Assert.Equal("video copy indices: [] | audio out: none | subtitles out: #2 und", RemuxPassVisibility.SummarizeRemuxPlan(plan));
    }

    [Theory]
    [InlineData("""{"rejected_without_manager":true,"rejection_summary":"Too small","relative_media_path":"a/film.mkv"}""", "Rejected film.mkv: Too small")]
    [InlineData("""{"rejected_without_manager":true,"rejection_summary":"","relative_media_path":"a/film.mkv"}""", "Rejected film.mkv")]
    [InlineData("""{"outcome":"live_output_written"}""", "unknown file was processed successfully")]
    [InlineData("""{"outcome":"live_output_written","relative_media_path":"   "}""", "unknown file was processed successfully")]
    [InlineData("""{"ok":false,"relative_media_path":"film.mkv"}""", "film.mkv could not be checked")]
    [InlineData("""{"ok":true,"relative_media_path":"film.mkv"}""", "File processing finished")]
    public void An_activity_title_names_the_file_and_what_happened(string payload, string expected) =>
        Assert.Equal(expected, RemuxPassVisibility.ActivityTitle(Payload(payload)));

    [Theory]
    [InlineData("""{"ok":false,"retry_scheduled":true}""", "retrying")]
    [InlineData("""{"ok":false,"outcome":"failed_during_execution"}""", "failed")]
    [InlineData("""{"outcome":"source_gone"}""", "skipped")]
    [InlineData("""{"outcome":"live_output_written"}""", "success")]
    public void A_clipped_payload_carries_the_result_the_standard_names(string payload, string expected)
    {
        var detail = RemuxPassVisibility.ClipForActivity(Payload(payload));

        Assert.Equal(expected, ((WireString)detail["result"]).Value);
    }

    [Fact]
    public void A_failure_with_nothing_queued_points_to_the_activity_page()
    {
        var plain = RemuxPassVisibility.ActivityDetail(Payload("""{"ok":false,"outcome":"failed_during_execution"}"""));
        var rejected = RemuxPassVisibility.ActivityDetail(Payload("""{"ok":false,"rejected_without_manager":true}"""));
        var queued = RemuxPassVisibility.ActivityDetail(Payload("""{"ok":false,"pass_through_queued":true}"""));

        Assert.Contains("use Try again there", plain, StringComparison.Ordinal);
        Assert.Contains("delete it, keep it, or process it again", rejected, StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", queued, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counts_read_the_length_of_a_list_a_string_or_an_object()
    {
        var detail = RemuxPassVisibility.ActivityDetail(Payload("""{"ok":true,"removed_audio":"abc","removed_subtitles":{"a":1,"b":2}}"""));

        Assert.Contains("\"audio_removed\":3", detail, StringComparison.Ordinal);
        Assert.Contains("\"subtitles_removed\":2", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_trigger_is_reported_as_the_worker()
    {
        var detail = RemuxPassVisibility.ActivityDetail(Payload("""{"ok":true,"trigger":"mystery"}"""));

        Assert.Contains("\"trigger\":\"worker\"", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_argv_is_cut_for_the_activity_log()
    {
        var argv = string.Join(',', Enumerable.Range(0, 70).Select(i => $"\"a{i}\""));

        var clipped = RemuxPassVisibility.ClipForActivity(Payload($$"""{"ok":true,"ffmpeg_argv":[{{argv}}]}"""));

        Assert.Equal(65, ((WireArray)clipped["ffmpeg_argv"]).Items.Count);
        Assert.True(((WireBool)clipped["ffmpeg_argv_truncated"]).Value);
    }
}
