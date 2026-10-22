using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>Whether a file already carries the track names a plan would write, for audio, subtitles and video.</summary>
public sealed class RemuxChangeDetectionEdgeTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    private static readonly ProbeStreamInfo Video = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");

    private static ProbeStreamInfo Audio(string title) =>
        Stream($$$"""{"index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "disposition": {"default": 1}, "tags": {"language": "eng", "title": "{{{title}}}"}}""");

    private static ProbeStreamInfo Subtitle(string title) =>
        Stream($$$"""{"index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng", "title": "{{{title}}}"}}""");

    private static ProcessingRulesConfig Config(MetadataRules metadata) => RemuxRules.DefaultConfig() with
    {
        SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
        SubtitleLangs = ["eng"],
        Metadata = metadata,
    };

    private static (RemuxPlan Plan, string AudioName, string SubtitleName) PlanWithNames()
    {
        var metadata = new MetadataRules { StandardizeTrackNames = true };
        var plan = RemuxRules.PlanRemux([Video], [Audio("")], [Subtitle("")], Config(metadata));
        Assert.NotNull(plan);
        return (plan!, TrackNaming.RenderTrackName(metadata, plan.Audio[0]), TrackNaming.RenderTrackName(metadata, plan.Subtitles[0]));
    }

    [Fact]
    public void Tracks_that_already_carry_the_standard_names_need_no_remux()
    {
        var (plan, audioName, subtitleName) = PlanWithNames();

        Assert.False(RemuxRules.IsRemuxRequired(plan, [Audio(audioName)], [Subtitle(subtitleName)]));
    }

    [Fact]
    public void An_audio_track_with_another_name_needs_a_remux()
    {
        var (plan, _, subtitleName) = PlanWithNames();

        Assert.True(RemuxRules.IsRemuxRequired(plan, [Audio("something else")], [Subtitle(subtitleName)]));
    }

    [Fact]
    public void A_subtitle_track_with_another_name_needs_a_remux()
    {
        var (plan, audioName, _) = PlanWithNames();

        Assert.True(RemuxRules.IsRemuxRequired(plan, [Audio(audioName)], [Subtitle("something else")]));
    }

    [Fact]
    public void Clearing_video_names_needs_a_remux_only_when_a_kept_video_stream_has_one()
    {
        var plan = RemuxRules.PlanRemux([Video], [Audio("")], [], Config(new MetadataRules { ClearVideoTrackNames = true }));
        Assert.NotNull(plan);
        var named = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264", "tags": {"title": "x265-GROUP"}}""");

        Assert.True(RemuxRules.IsRemuxRequired(plan!, [Audio("")], [], [named]));
        Assert.False(RemuxRules.IsRemuxRequired(plan!, [Audio("")], [], [Video]));
        Assert.False(RemuxRules.IsRemuxRequired(plan!, [Audio("")], [], []));
    }

    [Fact]
    public void Without_a_video_probe_a_video_name_is_assumed_to_be_there()
    {
        var plan = RemuxRules.PlanRemux([Video], [Audio("")], [], Config(new MetadataRules { ClearVideoTrackNames = true }));
        Assert.NotNull(plan);

        Assert.True(RemuxRules.IsRemuxRequired(plan!, [Audio("")], []));
    }
}
