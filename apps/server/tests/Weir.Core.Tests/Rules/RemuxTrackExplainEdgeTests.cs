using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>The track-by-track account for the cases the main explanation tests leave out: images and attachments kept, and unusual audio and subtitle tracks.</summary>
public sealed class RemuxTrackExplainEdgeTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    private static readonly ProbeStreamInfo Video = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");

    private static readonly ProbeStreamInfo Poster = Stream("""{"index": 1, "codec_type": "video", "codec_name": "mjpeg", "disposition": {"attached_pic": 1}}""");

    private static readonly ProbeStreamInfo Font = Stream("""{"index": 5, "codec_type": "attachment", "codec_name": "ttf", "tags": {"filename": "Arial.ttf"}}""");

    private static readonly ProbeStreamInfo English =
        Stream("""{"index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng"}}""");

    private static ProcessingRulesConfig KeepEnglishSubtitles() =>
        RemuxRules.DefaultConfig() with { SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected, SubtitleLangs = ["eng"] };

    [Fact]
    public void An_embedded_image_is_explained_as_kept_when_images_are_not_being_removed()
    {
        var decision = RemuxRules.ExplainTracks([Video, Poster], [English], [], RemuxRules.DefaultConfig()).Single(d => d.Kind == "image");

        Assert.True(decision.WouldKeep);
        Assert.Equal(1, decision.InputIndex);
    }

    [Fact]
    public void An_attachment_is_explained_as_kept_when_attachments_are_not_being_removed()
    {
        var decision = RemuxRules.ExplainTracks([Video], [English], [], RemuxRules.DefaultConfig(), [Font]).Single(d => d.Kind == "attachment");

        Assert.True(decision.WouldKeep);
        Assert.Equal("Attachment kept because remove attachments is off.", decision.Reason);
    }

    [Fact]
    public void An_attachment_is_explained_as_removed_when_attachments_are_being_removed()
    {
        var config = RemuxRules.DefaultConfig() with { Metadata = new MetadataRules { RemoveAttachments = true } };

        var decision = RemuxRules.ExplainTracks([Video], [English], [], config, [Font]).Single(d => d.Kind == "attachment");

        Assert.False(decision.WouldKeep);
        Assert.StartsWith("Removed ", decision.Reason, StringComparison.Ordinal);
        Assert.EndsWith(" because remove attachments is enabled.", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Arial.ttf", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void When_every_audio_track_is_commentary_none_is_selected_and_each_is_explained_as_removed_commentary()
    {
        var commentary = Stream("""{"index": 3, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng", "title": "Director commentary"}}""");
        var config = RemuxRules.DefaultConfig() with { RemoveCommentary = true };

        var decision = Assert.Single(RemuxRules.ExplainTracks([Video], [commentary], [], config), d => d.Kind == "audio");

        Assert.False(decision.WouldKeep);
        Assert.Contains("commentary", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Under_the_strict_policy_audio_in_another_language_is_explained_as_removed_with_nothing_selected()
    {
        var config = RemuxRules.DefaultConfig() with
        {
            AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict,
            PrimaryAudioLang = "jpn",
        };

        var decision = Assert.Single(RemuxRules.ExplainTracks([Video], [English], [], config), d => d.Kind == "audio");

        Assert.False(decision.WouldKeep);
        Assert.Equal("Removed: no audio track could be selected.", decision.Reason);
    }

    [Fact]
    public void A_commentary_track_is_kept_in_the_explanation_when_commentary_is_not_being_removed()
    {
        var commentary = Stream("""{"index": 3, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "eng", "title": "Director commentary"}}""");
        var config = RemuxRules.DefaultConfig() with { RemoveCommentary = false };

        var decisions = RemuxRules.ExplainTracks([Video], [English, commentary], [], config);

        Assert.Equal(1, decisions.Count(d => d.Kind == "audio" && d.WouldKeep));
    }

    [Fact]
    public void A_hearing_impaired_subtitle_is_explained_as_removed_by_its_flag_or_by_its_name()
    {
        var flagged = Stream("""{"index": 3, "codec_type": "subtitle", "disposition": {"hearing_impaired": 1}, "tags": {"language": "eng"}}""");
        var named = Stream("""{"index": 4, "codec_type": "subtitle", "tags": {"language": "eng", "title": "English (SDH)"}}""");
        var config = KeepEnglishSubtitles() with { RemoveHearingImpairedSubs = true };

        var decisions = RemuxRules.ExplainTracks([Video], [English], [flagged, named], config);

        Assert.Equal(
            "Removed: hearing-impaired subtitle (its hearing-impaired flag), and remove hearing-impaired subtitles is enabled.",
            decisions.Single(d => d.InputIndex == 3).Reason);
        Assert.Equal(
            "Removed: hearing-impaired subtitle (its name), and remove hearing-impaired subtitles is enabled.",
            decisions.Single(d => d.InputIndex == 4).Reason);
    }

    [Fact]
    public void A_subtitle_whose_name_says_forced_is_explained_as_kept_and_counted_as_forced()
    {
        var forced = Stream("""{"index": 3, "codec_type": "subtitle", "tags": {"language": "eng", "title": "Forced"}}""");

        var decision = RemuxRules.ExplainTracks([Video], [English], [forced], KeepEnglishSubtitles() with { PreserveForcedSubs = true }).Single(d => d.InputIndex == 3);

        Assert.True(decision.WouldKeep);
        Assert.EndsWith(" Counts as forced because its name says so.", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_name_is_not_mentioned_when_forced_subtitles_are_not_being_preserved()
    {
        var forced = Stream("""{"index": 3, "codec_type": "subtitle", "tags": {"language": "eng", "title": "Forced"}}""");

        var decision = RemuxRules.ExplainTracks([Video], [English], [forced], KeepEnglishSubtitles() with { PreserveForcedSubs = false }).Single(d => d.InputIndex == 3);

        Assert.DoesNotContain("forced", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtitle_with_no_language_is_explained_as_undetermined_unless_every_language_is_kept()
    {
        var untagged = Stream("""{"index": 3, "codec_type": "subtitle", "tags": {}}""");

        var removed = RemuxRules.ExplainTracks([Video], [English], [untagged], KeepEnglishSubtitles()).Single(d => d.InputIndex == 3);
        var kept = RemuxRules.ExplainTracks([Video], [English], [untagged], KeepEnglishSubtitles() with { SubtitleMode = RemuxRuleValues.SubtitleModeKeepAll })
            .Single(d => d.InputIndex == 3);

        Assert.Equal("Removed: language 'und' is not in the configured subtitle languages.", removed.Reason);
        Assert.True(kept.WouldKeep);
        Assert.Equal("Kept: the saved rules keep every subtitle language.", kept.Reason);
    }
}
