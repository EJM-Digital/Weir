using Weir.Core.Rules;

namespace Weir.Core.Tests.Rules;

/// <summary>The labels shown for a track's codec, channel layout, language and the before/after lines built from them.</summary>
public sealed class RemuxDisplayLabelTests
{
    private static PlannedTrack Subtitle(string lang) => new() { InputIndex = 2, LangLabel = lang, Kind = TrackKind.Subtitle };

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("TrueHD", "TrueHD")]
    [InlineData("dts_hd", "DTS-HD MA")]
    [InlineData("eac3", "E-AC-3")]
    [InlineData("ac3", "AC-3")]
    [InlineData("aac", "AAC")]
    [InlineData("flac", "FLAC")]
    [InlineData("opus", "Opus")]
    [InlineData("vorbis", "Vorbis")]
    [InlineData("pcm_s24le", "PCM")]
    [InlineData("mp3", "MP3")]
    [InlineData("wavpack_lossless", "WAVPACK LOSSLESS")]
    public void A_codec_is_shown_by_its_familiar_name(string? codec, string expected) =>
        Assert.Equal(expected, RemuxDisplay.CodecDisplayName(codec));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1.0")]
    [InlineData(2, "2.0")]
    [InlineData(6, "5.1")]
    [InlineData(8, "7.1")]
    [InlineData(4, "4 ch")]
    public void A_channel_count_is_shown_as_its_layout(long channels, string expected) =>
        Assert.Equal(expected, RemuxDisplay.ChannelsDisplay(channels));

    [Fact]
    public void No_language_is_a_dash_or_blank_depending_on_where_it_is_shown()
    {
        Assert.Equal("—", RemuxDisplay.LangDisplay(null));
        Assert.Equal(string.Empty, RemuxDisplay.LangDisplayOrBlank(null));
    }

    [Fact]
    public void Subtitle_lines_read_none_when_nothing_is_kept_or_nothing_has_a_language()
    {
        Assert.Equal("None", RemuxDisplay.SubtitleAfterLineFromPlan(new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [Subtitle("eng")] }, removeAll: true));
        Assert.Equal("None", RemuxDisplay.SubtitleAfterLineFromPlan(new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] }, removeAll: false));
        Assert.Equal("None", RemuxDisplay.SubtitleAfterLineFromPlan(new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [Subtitle(string.Empty)] }, removeAll: false));
        Assert.Equal("—", RemuxDisplay.SubtitleBeforeLineFromProbe([]));
        Assert.Equal("Undetermined", RemuxDisplay.SubtitleBeforeLineFromProbe([ProbeStreamInfo.Parse("""{"index": 2, "codec_type": "subtitle"}""")]));
    }

    [Fact]
    public void Track_lines_with_nothing_to_show_collapse_to_a_dash()
    {
        Assert.Equal("—", RemuxDisplay.JoinTrackLines([null, "  ", "—"]));
        Assert.Equal("English 5.1 AAC · French", RemuxDisplay.JoinTrackLines(["English 5.1 AAC", null, " French "]));
        Assert.Equal("—", RemuxDisplay.FormatPlannedAudioTrackLine(new PlannedTrack { InputIndex = 1, LangLabel = string.Empty, Kind = TrackKind.Audio }));
    }
}
