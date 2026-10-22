using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>The preview rows and size estimate for files with streams that have no index, no notes, or a bit rate that is not a number.</summary>
public sealed class RulesPreviewEdgeTests
{
    private const string Odd = """
        {
          "streams": [
            {"index": 0, "codec_type": "video", "codec_name": "h264"},
            {"codec_type": "audio", "codec_name": "aac", "channels": 2},
            {"index": 1, "codec_type": "audio", "codec_name": "aac", "tags": {"language": "eng"}},
            {"index": 2, "codec_type": "audio", "codec_name": "dts", "channels": 6, "bit_rate": "1500000", "tags": {"language": "jpn"}},
            {"index": 3, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng"}},
            {"index": 4, "codec_type": "subtitle", "codec_name": "subrip", "bit_rate": "8000", "tags": {"language": "spa"}},
            {"index": 5, "codec_type": "attachment", "codec_name": "ttf", "bit_rate": "9999", "tags": {"filename": "Arial.ttf"}},
            {"index": 6, "codec_type": "data", "codec_name": "bin_data"},
            {"index": 7, "codec_type": "audio", "codec_name": "aac", "channels": 2, "bit_rate": "N/A", "tags": {"language": "fre"}}
          ]
        }
        """;

    private static (ProbeResult Probe, RemuxPlan Plan) Plan()
    {
        var probe = ProbeResult.Parse(Odd);
        var (video, audio, subtitles) = RemuxRules.SplitStreams(probe);
        var config = RemuxRules.DefaultConfig() with
        {
            PrimaryAudioLang = "eng",
            SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
            SubtitleLangs = ["eng"],
        };
        return (probe, RemuxRules.PlanRemux(video, audio, subtitles, config)!);
    }

    [Fact]
    public void Streams_with_no_index_and_streams_that_are_not_media_get_no_row()
    {
        var (probe, plan) = Plan();

        var rows = RulesPreview.BuildTrackRows(probe, plan);

        Assert.Equal([0, 1, 2, 3, 4, 7], rows.Select(r => r.Index));
    }

    [Fact]
    public void An_audio_stream_that_reports_no_channel_count_shows_none()
    {
        var (probe, plan) = Plan();

        var row = RulesPreview.BuildTrackRows(probe, plan).Single(r => r.Index == 1);

        Assert.Equal(0, row.Channels);
        Assert.Equal("audio", row.Type);
    }

    [Fact]
    public void A_row_with_no_note_of_its_own_gets_the_usual_reason_for_its_kind_and_outcome()
    {
        var (probe, plan) = Plan();

        var rows = RulesPreview.BuildTrackRows(probe, plan with { AudioSelectionNotes = [] }).ToDictionary(r => r.Index);

        Assert.Equal(["Video track kept unchanged."], rows[0].Reasons);
        Assert.Equal(["Selected as the preferred audio track."], rows[1].Reasons);
        Assert.Equal(["Removed — a different audio track was selected."], rows[2].Reasons);
        Assert.Equal(["Subtitle language matches the configured selection."], rows[3].Reasons);
        Assert.Equal(["Removed — language not in the configured subtitle selection."], rows[4].Reasons);
    }

    [Fact]
    public void A_dropped_embedded_image_is_explained_as_dropped_by_the_metadata_rules()
    {
        var probe = ProbeResult.Parse("""
            {"streams": [
              {"index": 0, "codec_type": "video", "codec_name": "h264"},
              {"index": 1, "codec_type": "video", "codec_name": "mjpeg", "disposition": {"attached_pic": 1}},
              {"index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng"}}]}
            """);
        var (video, audio, subtitles) = RemuxRules.SplitStreams(probe);
        var config = RemuxRules.DefaultConfig() with { Metadata = new MetadataRules { RemoveImages = true } };
        var plan = RemuxRules.PlanRemux(video, audio, subtitles, config, RemuxRules.AttachmentStreams(probe))!;

        var row = RulesPreview.BuildTrackRows(probe, plan with { AudioSelectionNotes = [] }).Single(r => r.Index == 1);

        Assert.False(row.Kept);
        Assert.Equal(["Embedded image dropped by the metadata rules."], row.Reasons);
    }

    [Fact]
    public void A_note_that_names_a_stream_number_too_large_to_exist_is_ignored()
    {
        var (probe, plan) = Plan();

        var rows = RulesPreview.BuildTrackRows(probe, plan with { AudioSelectionNotes = ["Dropped stream 99999999999999999999 for no reason.", "Kept stream 3 on purpose."] });

        Assert.Equal(["Kept stream 3 on purpose."], rows.Single(r => r.Index == 3).Reasons);
    }

    [Fact]
    public void The_estimate_counts_only_dropped_tracks_that_state_a_bit_rate()
    {
        var (probe, plan) = Plan();

        // Dropped: audio 2 (1,500,000 bits/s) and subtitle 4 (8,000 bits/s); audio 7 says "N/A" and counts for nothing.
        var estimate = RulesPreview.EstimateDroppedBytes(probe, plan, 100);

        Assert.Equal((1_500_000 + 8_000) / 8 * 100, estimate);
    }

    [Fact]
    public void The_estimate_treats_every_audio_track_as_dropped_when_the_plan_keeps_none()
    {
        var (probe, plan) = Plan();

        var estimate = RulesPreview.EstimateDroppedBytes(probe, plan with { Audio = [] }, 10);

        Assert.Equal((1_500_000 + 8_000) / 8 * 10, estimate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void There_is_no_estimate_without_a_duration(double? duration)
    {
        var (probe, plan) = Plan();

        Assert.Null(RulesPreview.EstimateDroppedBytes(probe, plan, duration));
    }

    [Fact]
    public void There_is_no_estimate_when_nothing_that_is_dropped_states_a_bit_rate()
    {
        var probe = ProbeResult.Parse("""{"streams": [{"index": 0, "codec_type": "video", "codec_name": "h264"}, {"index": 1, "codec_type": "audio", "codec_name": "aac", "tags": {"language": "eng"}}]}""");
        var (video, audio, subtitles) = RemuxRules.SplitStreams(probe);
        var plan = RemuxRules.PlanRemux(video, audio, subtitles, RemuxRules.DefaultConfig())!;

        Assert.Null(RulesPreview.EstimateDroppedBytes(probe, plan, 60));
    }
}
