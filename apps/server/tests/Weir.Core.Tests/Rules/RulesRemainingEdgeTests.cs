using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>The last edges of the rules: settings that are missing, tracks with no language or name, and the wording for rarer rejections.</summary>
public sealed class RulesRemainingEdgeTests
{
    private static ProbeStreamInfo Stream(string json) => ProbeStreamInfo.Parse(json);

    private static readonly ProbeStreamInfo Video = Stream("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");

    private static ProbeStreamInfo Audio(int index, string? language, int channels = 2, string? title = null, string codec = "aac")
    {
        var tags = new List<string>();
        if (language is not null)
        {
            tags.Add($"\"language\": \"{language}\"");
        }

        if (title is not null)
        {
            tags.Add($"\"title\": \"{title}\"");
        }

        return Stream(
            "{\"index\": " + index + ", \"codec_type\": \"audio\", \"codec_name\": \"" + codec + "\", \"channels\": " + channels +
            ", \"bit_rate\": \"" + (channels * 64000) + "\", \"tags\": {" + string.Join(", ", tags) + "}}");
    }

    [Fact]
    public void Rules_that_are_not_strict_about_language_explain_a_file_with_no_audio_left_without_naming_a_language()
    {
        var config = RemuxRules.DefaultConfig() with { AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality };

        var rejection = AudioRejectionExplanation.Explain(config, [Audio(1, "eng")], "Films");

        Assert.Equal("the \"Films\" rules leave no audio to keep", rejection.Summary);
    }

    [Theory]
    [InlineData("fre-CA", "French (Canada)")]
    [InlineData("jpn", "Japanese")]
    public void A_first_choice_language_is_named_whether_it_is_a_plain_code_or_a_regional_variant(string primary, string name)
    {
        var config = RemuxRules.DefaultConfig() with { AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict, PrimaryAudioLang = primary };

        var rejection = AudioRejectionExplanation.Explain(config, [Audio(1, "eng")], null);

        Assert.Equal($"no {name} audio for your rules", rejection.Summary);
    }

    [Theory]
    [InlineData(1, "It has English audio.")]
    [InlineData(2, "It has English and Japanese audio.")]
    [InlineData(3, "It has English, Japanese and Spanish audio.")]
    public void The_languages_a_file_does_have_are_listed_in_plain_words(int count, string sentence)
    {
        var config = RemuxRules.DefaultConfig() with { AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict, PrimaryAudioLang = "fre" };
        var audio = new[] { Audio(1, "eng"), Audio(2, "jpn"), Audio(3, "spa") }.Take(count).ToList();

        var rejection = AudioRejectionExplanation.Explain(config, audio, null);

        Assert.Contains(sentence, rejection.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dub_named_audio_description_track_is_noted_as_such_when_it_is_kept()
    {
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, "eng", title: "English Audio Description")], [], config);

        Assert.Contains(plan!.AudioSelectionNotes, note => note.Contains("audio-description track from its name", StringComparison.Ordinal));
    }

    [Fact]
    public void A_track_with_no_language_ranks_after_one_that_matches_when_no_track_matches_the_tiers()
    {
        var config = RemuxRules.DefaultConfig() with { PrimaryAudioLang = "eng", AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsQuality };

        var plan = RemuxRules.PlanRemux([Video], [Audio(1, null, channels: 6), Audio(2, "jpn", channels: 2)], [], config);

        Assert.Equal([2], plan!.Audio.Select(track => track.InputIndex));
    }

    [Fact]
    public void Every_codec_the_track_lines_name_has_its_own_label()
    {
        string[] codecs = ["truehd", "dts_hd", "eac3", "ac3", "aac", "flac", "opus", "vorbis", "pcm_s16le", "pcm_s24le", "pcm_s32le", "mp3", "dts", ""];

        Assert.Equal(
            ["TrueHD", "DTS-HD MA", "E-AC-3", "AC-3", "AAC", "FLAC", "Opus", "Vorbis", "PCM", "PCM", "PCM", "MP3", "DTS", string.Empty],
            codecs.Select(RemuxDisplay.CodecDisplayName));
        Assert.Equal(string.Empty, RemuxDisplay.CodecDisplayName(null));
    }

    [Theory]
    [InlineData('⺀', true)]
    [InlineData('鿿', true)]
    [InlineData('豈', true)]
    [InlineData('﫿', true)]
    [InlineData('a', false)]
    [InlineData('é', false)]
    public void Chinese_japanese_and_korean_characters_are_told_from_the_rest(char character, bool cjk)
    {
        Assert.Equal(cjk, LanguageVariants.IsCjk(character));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("---")]
    [InlineData("xx-419")]
    public void A_language_tag_with_no_usable_parts_gives_no_variant(string tag)
    {
        Assert.False(LanguageVariants.Detect(null, "fre", tag).Found);
    }

    [Fact]
    public void A_united_nations_region_number_is_a_variant_when_the_table_knows_it()
    {
        Assert.Equal("spa-419", LanguageVariants.Detect(null, "spa", "es-419").Identifier);
    }

    [Fact]
    public void A_variant_identifier_is_only_one_the_table_knows()
    {
        Assert.False(LanguageVariants.Matches(string.Empty, "fre", null));
        Assert.False(LanguageVariants.IsVariantIdentifier(null));
        Assert.True(LanguageVariants.IsVariantIdentifier("fre-CA"));
    }

    [Theory]
    [InlineData("""{"codec_name": "mjpeg", "avg_frame_rate": "0/0"}""", true)]
    [InlineData("""{"codec_name": "mjpeg", "avg_frame_rate": "0/1"}""", true)]
    [InlineData("""{"codec_name": "mjpeg"}""", true)]
    [InlineData("""{"codec_name": "mjpeg", "avg_frame_rate": "24/1"}""", false)]
    [InlineData("""{"codec_name": "h264"}""", false)]
    public void A_still_image_is_told_from_a_video_by_its_frame_rate(string stream, bool image)
    {
        Assert.Equal(image, MetadataStreams.IsImageStream(Stream(stream)));
    }

    [Theory]
    [InlineData("""{"codec_name": "png", "width": 600, "height": 900}""", "embedded image (png, 600x900)")]
    [InlineData("""{"codec_name": "png", "width": 600}""", "embedded image (png, unknown size)")]
    public void An_embedded_image_is_described_with_its_size_when_it_has_one(string stream, string expected)
    {
        Assert.Equal(expected, MetadataStreams.DescribeImageStream(Stream(stream)));
    }

    [Fact]
    public void Languages_to_add_to_the_originals_may_be_missing()
    {
        Assert.Empty(OriginalLanguage.ParseAdditionalLanguages(null));
        Assert.Equal(["fre", "ger"], OriginalLanguage.ParseAdditionalLanguages("fr, de, fr"));
        Assert.False(new TitleMetadata { OriginalLanguage = "  " }.HasOriginalLanguage);
        Assert.True(new TitleMetadata { OriginalLanguage = "fr" }.HasOriginalLanguage);
    }

    [Fact]
    public void A_kept_mode_or_strategy_that_is_missing_reads_as_the_default()
    {
        Assert.Equal(RemuxRuleValues.AudioKeepModeSingle, RemuxRules.NormalizeAudioKeepMode(null));
        Assert.Equal(RemuxRuleValues.SubtitleStrategyTextFirst, RemuxRules.NormalizeSubtitleQualityStrategy(null));
    }

    [Fact]
    public void Lists_typed_into_settings_may_be_missing_and_end_their_lines_in_any_way()
    {
        Assert.Empty(RemuxRules.ParseSubtitleLangsCsv(null));
        Assert.Empty(RemuxRules.ParsePathLines(null));
        Assert.Equal(["a", "b"], RemuxRules.ParsePathLines("a\rb"));
        Assert.Equal(["a", "b"], RemuxRules.ParsePathLines("a\r\nb"));
        Assert.Equal(["a"], RemuxRules.ParsePathLines("a\r"));
    }

    [Fact]
    public void A_track_name_template_that_is_missing_has_nothing_to_refuse()
    {
        TrackNaming.ValidateTemplate(null);
        Assert.Throws<TrackNameTemplateException>(() => TrackNaming.ValidateTemplate("{nonsense}"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_flag_is_from_the_disposition_only_when_it_is_set_and_the_disposition_said_so(bool value, bool fromDisposition, bool expected)
    {
        var flag = new TrackFlag(value, fromDisposition ? TrackFlagSource.Disposition : TrackFlagSource.Name);

        Assert.Equal(expected, flag.FromDisposition);
    }

    [Theory]
    [InlineData(false, "content tier (main, then dub/audio description, then commentary)")]
    [InlineData(true, "content tier (commentary, then dub/audio description, then main)")]
    public void The_content_tier_sorter_describes_its_direction(bool reversed, string description)
    {
        Assert.Equal(description, new TrackSorter("content_tier", null, reversed).Describe());
    }

    [Theory]
    [InlineData(false, false, false, false, 0L, 2L)]
    [InlineData(false, true, false, false, 1L, 1L)]
    [InlineData(false, false, true, false, 1L, 1L)]
    [InlineData(false, false, false, true, 2L, 0L)]
    public void The_content_tier_orders_main_then_dub_or_description_then_commentary(bool unused, bool dub, bool description, bool commentary, long tier, long reversedTier)
    {
        Assert.False(unused);
        var track = new SortableTrack { Dub = dub, AudioDescription = description, Commentary = commentary };

        Assert.Equal([tier], TrackSorters.SorterKeyComponent(new TrackSorter("content_tier"), track));
        Assert.Equal([reversedTier], TrackSorters.SorterKeyComponent(new TrackSorter("content_tier", null, true), track));
    }

    [Fact]
    public void Kept_tracks_with_no_title_are_renamed_when_names_are_standardised()
    {
        var metadata = new MetadataRules { StandardizeTrackNames = true };
        var config = RemuxRules.DefaultConfig() with
        {
            SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected,
            SubtitleLangs = ["eng"],
            Metadata = metadata,
        };
        var subtitle = Stream("""{"index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng"}}""");
        var untitled = Audio(1, "eng");
        var plan = RemuxRules.PlanRemux([Video], [untitled], [subtitle], config)!;

        Assert.True(RemuxRules.IsRemuxRequired(plan, [untitled], [subtitle]));
    }
}
