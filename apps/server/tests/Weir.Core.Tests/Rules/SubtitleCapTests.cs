using System.Text.Json;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>
/// Which subtitle survives the per-language cap and how each dropped one is described: the format words, the order of
/// the formats and types under each strategy, and the wording for tracks with no language.
/// </summary>
public sealed class SubtitleCapTests
{
    private static readonly ProbeStreamInfo Video = ProbeStreamInfo.Parse("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");

    private static readonly ProbeStreamInfo Audio =
        ProbeStreamInfo.Parse("""{"index": 10, "codec_type": "audio", "codec_name": "aac", "channels": 2, "bit_rate": "128000", "tags": {"language": "eng"}}""");

    private static ProbeStreamInfo Sub(int index, string codec, string title = "", string? language = "eng", bool forced = false) =>
        ProbeStreamInfo.Parse(
            "{\"index\": " + index + ", \"codec_type\": \"subtitle\", \"codec_name\": " + JsonSerializer.Serialize(codec) +
            ", \"disposition\": {\"forced\": " + (forced ? 1 : 0) + "}" +
            ", \"tags\": {" + (language is null ? string.Empty : "\"language\": \"" + language + "\", ") + "\"title\": " + JsonSerializer.Serialize(title) + "}}");

    private static RemuxPlan Plan(ProcessingRulesConfig config, params ProbeStreamInfo[] subtitles) =>
        RemuxRules.PlanRemux([Video], [Audio], subtitles, config)!;

    private static ProcessingRulesConfig Config(string strategy = RemuxRuleValues.SubtitleStrategyTextFirst) => RemuxRules.DefaultConfig() with
    {
        SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
        SubtitleLangs = ["eng"],
        SubtitleMaxPerLanguage = 1,
        SubtitleQualityStrategy = strategy,
    };

    [Theory]
    [InlineData("hdmv_pgs_subtitle", "PGS")]
    [InlineData("pgs", "PGS")]
    [InlineData("dvd_subtitle", "VobSub")]
    [InlineData("vobsub", "VobSub")]
    [InlineData("dvb_subtitle", "DVB subtitle")]
    [InlineData("dvbsub", "DVB subtitle")]
    [InlineData("xsub", "XSUB")]
    [InlineData("", "unknown format")]
    [InlineData("eia_608", "EIA_608")]
    public void A_dropped_subtitle_is_named_by_its_format(string droppedCodec, string word)
    {
        var plan = Plan(Config(), Sub(1, "subrip"), Sub(2, droppedCodec));

        Assert.Equal([1], plan.Subtitles.Select(t => t.InputIndex));
        Assert.Contains($"Dropped the second English subtitle (stream 2); kept the text track over {word}.", plan.AudioSelectionNotes);
    }

    [Fact]
    public void The_cap_counts_dropped_tracks_by_their_place_in_the_file_even_past_the_tenth()
    {
        var subs = Enumerable.Range(1, 12).Select(i => Sub(i, "subrip")).ToArray();

        var plan = Plan(Config(), subs);

        Assert.Equal([1], plan.Subtitles.Select(t => t.InputIndex));
        Assert.Contains("Dropped the tenth English subtitle (stream 10); kept the text track over text.", plan.AudioSelectionNotes);
        Assert.Contains("Dropped the 11th English subtitle (stream 11); kept the text track over text.", plan.AudioSelectionNotes);
        Assert.Contains("Dropped the 12th English subtitle (stream 12); kept the text track over text.", plan.AudioSelectionNotes);
    }

    [Theory]
    [InlineData(RemuxRuleValues.SubtitleStrategyTextFirst, 2)]
    [InlineData(RemuxRuleValues.SubtitleStrategyImageFirst, 1)]
    public void A_format_neither_text_nor_image_ranks_between_the_two(string strategy, int keptIndex)
    {
        // Stream 1 is an image, stream 2 text, stream 3 a format Weir does not know: with one slot, the unknown one never wins.
        var plan = Plan(Config(strategy), Sub(1, "hdmv_pgs_subtitle"), Sub(2, "subrip"), Sub(3, "mystery_format"));

        Assert.Equal([keptIndex], plan.Subtitles.Select(t => t.InputIndex));
    }

    [Fact]
    public void An_unknown_format_is_kept_over_the_worse_of_text_and_image()
    {
        var textFirst = Plan(Config(), Sub(1, "hdmv_pgs_subtitle"), Sub(2, "mystery_format"));
        var imageFirst = Plan(Config(RemuxRuleValues.SubtitleStrategyImageFirst), Sub(1, "subrip"), Sub(2, "mystery_format"));

        Assert.Equal([2], textFirst.Subtitles.Select(t => t.InputIndex));
        Assert.Equal([2], imageFirst.Subtitles.Select(t => t.InputIndex));
    }

    [Theory]
    [InlineData(RemuxRuleValues.SubtitleStrategyTextFirst, 1)]
    [InlineData(RemuxRuleValues.SubtitleStrategyImageFirst, 1)]
    [InlineData(RemuxRuleValues.SubtitleStrategyAccessibility, 2)]
    public void A_forced_track_not_kept_by_preserve_forced_competes_last(string strategy, int keptIndex)
    {
        // Stream 1 is regular, stream 2 is hearing-impaired, stream 3 is forced but preserve-forced is off, so it takes part in the cap.
        var config = Config(strategy) with { PreserveForcedSubs = false };

        var plan = Plan(config, Sub(3, "subrip", forced: true), Sub(1, "subrip"), Sub(2, "subrip", "English (SDH)"));

        Assert.Equal([keptIndex], plan.Subtitles.Select(t => t.InputIndex));
    }

    [Fact]
    public void A_forced_track_loses_to_a_regular_one_under_every_strategy_when_it_is_not_exempt()
    {
        foreach (var strategy in new[] { RemuxRuleValues.SubtitleStrategyTextFirst, RemuxRuleValues.SubtitleStrategyAccessibility })
        {
            var plan = Plan(Config(strategy) with { PreserveForcedSubs = false }, Sub(1, "subrip", forced: true), Sub(2, "subrip"));

            Assert.Equal([2], plan.Subtitles.Select(t => t.InputIndex));
        }
    }

    [Fact]
    public void Tracks_with_no_language_are_described_as_undetermined()
    {
        var config = Config() with { SubtitleMode = RemuxRuleValues.SubtitleModeKeepAll };

        var plan = Plan(config, Sub(1, "subrip", language: null), Sub(2, "hdmv_pgs_subtitle", language: null));

        Assert.Equal([1], plan.Subtitles.Select(t => t.InputIndex));
        Assert.Contains("Dropped the second Undetermined subtitle (stream 2); kept the text track over PGS.", plan.AudioSelectionNotes);
        Assert.Equal(["und"], plan.RemovedSubtitles);
    }

    [Fact]
    public void A_dropped_variant_track_is_named_by_its_variant()
    {
        var config = Config() with { SubtitleLangs = ["fre"] };

        var plan = Plan(config, Sub(1, "subrip", language: "fre"), Sub(2, "hdmv_pgs_subtitle", "Canadian French", language: "fre"));

        Assert.Contains(plan.AudioSelectionNotes, n => n.StartsWith("Dropped the second ", StringComparison.Ordinal) && n.Contains("French", StringComparison.Ordinal));
        Assert.Equal(["fre"], plan.RemovedSubtitles);
    }
}
