using System.Text.Json;
using Weir.Core.Media;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Media;

/// <summary>
/// The odd shapes ffprobe documents can take (missing fields, wrong types, unknown streams) as seen by the output
/// check, the expected-duration reader and the probe-field readers behind them.
/// </summary>
public sealed class RemuxOutputValidationEdgeTests
{
    private static RemuxPlan Plan(IReadOnlyList<PlannedTrack> subtitles) =>
        new()
        {
            VideoIndices = [0],
            Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Default = true, Kind = TrackKind.Audio }],
            Subtitles = subtitles,
            Metadata = new MetadataRules(),
        };

    private static JsonElement Probe(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private const string VideoAndEnglishAudio =
        """{"codec_type":"video"},{"codec_type":"audio","disposition":{"default":1},"tags":{"language":"eng"}}""";

    [Fact]
    public void A_stream_of_an_unplanned_type_is_a_track_count_mismatch()
    {
        var output = Probe($$"""{"format":{"duration":"10"},"streams":[{{VideoAndEnglishAudio}},{"codec_type":"data"}]}""");

        var error = Assert.Throws<MediaToolException>(() => RemuxOutputValidation.ValidateAgainstPlan(output, Plan([]), null, 10, [], []));

        Assert.Equal("Planned 2 tracks, output has 3.", error.Message);
    }

    [Fact]
    public void A_track_of_another_type_in_a_position_is_named_even_when_the_counts_match()
    {
        var plan = Plan([new PlannedTrack { InputIndex = 2, LangLabel = "eng", Kind = TrackKind.Subtitle }]);
        var output = Probe(
            """{"format":{"duration":"10"},"streams":[{"codec_type":"video"},{"codec_type":"subtitle","tags":{"language":"eng"}},{"codec_type":"audio","disposition":{"default":1},"tags":{"language":"eng"}}]}""");

        var error = Assert.Throws<MediaToolException>(() => RemuxOutputValidation.ValidateAgainstPlan(output, plan, null, 10, [], []));

        Assert.Equal("Planned output position 1 to be audio, output has subtitle.", error.Message);
    }

    [Fact]
    public void An_untagged_track_is_reported_as_undetermined()
    {
        var output = Probe("""{"format":{"duration":"10"},"streams":[{"codec_type":"video"},{"codec_type":"audio","disposition":{"default":1}}]}""");

        var error = Assert.Throws<MediaToolException>(() => RemuxOutputValidation.ValidateAgainstPlan(output, Plan([]), null, 10, [], []));

        Assert.Contains("is tagged 'und'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_with_no_measurable_duration_is_not_published()
    {
        var output = Probe($$"""{"format":{},"streams":[{{VideoAndEnglishAudio}}]}""");

        var error = Assert.Throws<MediaCompletenessException>(() => RemuxOutputValidation.ValidateAgainstPlan(output, Plan([]), null, 10, [], []));

        Assert.Contains("could not confirm the staged output duration", error.Message, StringComparison.Ordinal);
    }

    // --- expected duration -------------------------------------------------------------------

    [Theory]
    [InlineData("""[]""", null)]
    [InlineData("""{"streams":5}""", null)]
    [InlineData("""{"streams":[7,{"index":0,"duration":"12.5"}]}""", 12.5)]
    [InlineData("""{"streams":[{"duration":"3"},{"index":"x","duration":"4"}]}""", null)]
    [InlineData("""{"streams":[{"index":9,"duration":"99"},{"index":0,"duration":"8"}]}""", 8d)]
    [InlineData("""{"streams":[{"index":0,"duration":20},{"index":1,"duration":"10"}]}""", 20d)]
    [InlineData("""{"streams":[{"index":0,"duration":"10"},{"index":1,"duration":20.5}]}""", 20.5)]
    [InlineData("""{"streams":[{"index":0,"duration":true,"tags":{"DURATION":"00:01:00.000"}}]}""", 60d)]
    [InlineData("""{"streams":[{"index":0,"duration":1,"tags":{"DURATION":"00:00:30"}}]}""", 1d)]
    [InlineData("""{"streams":[{"index":0,"duration":-5,"tags":{"DURATION":"00:00:30"}}]}""", 30d)]
    [InlineData("""{"streams":[{"index":0,"duration":"-5"}]}""", null)]
    [InlineData("""{"streams":[{"index":0,"tags":"none"}]}""", null)]
    [InlineData("""{"streams":[{"index":0,"tags":{"DURATION":7}}]}""", null)]
    [InlineData("""{"streams":[{"index":0,"tags":{"DURATION":"soon"}}]}""", null)]
    [InlineData("""{"streams":[{"index":0,"tags":{"DURATION":"00:00:00.000"}}]}""", null)]
    public void The_expected_duration_comes_from_the_longest_kept_stream(string probe, double? expected) =>
        Assert.Equal(expected, RemuxOutputValidation.ExpectedDurationFromKeptStreams(Probe(probe), Plan([])));

    [Fact]
    public void A_kept_subtitle_stream_counts_towards_the_expected_duration()
    {
        var plan = Plan([new PlannedTrack { InputIndex = 2, LangLabel = "eng", Kind = TrackKind.Subtitle }]);

        Assert.Equal(40d, RemuxOutputValidation.ExpectedDurationFromKeptStreams(Probe("""{"streams":[{"index":2,"duration":"40"}]}"""), plan));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("not a time", null)]
    [InlineData("00:00:00", null)]
    [InlineData("01:02:03.5", 3723.5)]
    [InlineData(" 00:00:07 ", 7d)]
    public void A_matroska_duration_tag_is_read_as_seconds(string? text, double? expected) =>
        Assert.Equal(expected, RemuxOutputValidation.ParseTagsDuration(text));

    // --- probe fields ------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("MOV,MP4,m4a", "mov/mp4")]
    [InlineData(" Matroska,WebM ", "matroska/webm")]
    [InlineData("FLAC", "flac")]
    public void A_container_is_named_by_its_muxer_family(string? formatName, string expected) =>
        Assert.Equal(expected, RemuxOutputValidation.ContainerFamily(formatName));

    [Theory]
    [InlineData("""[]""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""{"format":3}""", null)]
    [InlineData("""{"format":{}}""", null)]
    [InlineData("""{"format":{"format_name":""}}""", null)]
    [InlineData("""{"format":{"format_name":"flac"}}""", "flac")]
    public void The_format_name_is_read_only_when_there_is_one(string probe, string? expected) =>
        Assert.Equal(expected, RemuxOutputValidation.FormatName(Probe(probe)));

    [Theory]
    [InlineData("""[]""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""{"format":3}""", null)]
    [InlineData("""{"format":{}}""", null)]
    [InlineData("""{"format":{"tags":"x"}}""", null)]
    [InlineData("""{"format":{"tags":{"title":""}}}""", null)]
    [InlineData("""{"format":{"tags":{"title":"A Film"}}}""", "A Film")]
    public void The_container_title_is_read_only_when_there_is_one(string probe, string? expected) =>
        Assert.Equal(expected, RemuxOutputValidation.FormatTitle(Probe(probe)));

    [Theory]
    [InlineData("""[]""", 0)]
    [InlineData("""{"streams":5}""", 0)]
    [InlineData("""{"streams":[1,{"codec_type":"video"}]}""", 1)]
    [InlineData("""{"streams":[{"codec_type":"attachment"},{"codec_type":"audio"}]}""", 1)]
    public void The_actual_layout_skips_what_is_not_a_track(string probe, int expectedCount) =>
        Assert.Equal(expectedCount, RemuxOutputValidation.ActualLayout(Probe(probe)).Count);

    [Theory]
    [InlineData("""{"codec_type":5}""", "", false, false, null)]
    [InlineData("""{"codec_type":"AUDIO","disposition":{"default":1,"forced":"1"},"tags":{"language":"fre"}}""", "audio", true, true, "fre")]
    [InlineData("""{"codec_type":"audio","disposition":"x","tags":"x"}""", "audio", false, false, null)]
    [InlineData("""{"codec_type":"audio","disposition":{"default":"x"},"tags":{"language":""}}""", "audio", false, false, null)]
    [InlineData("""{"codec_type":"audio","tags":{"language":4}}""", "audio", false, false, null)]
    public void A_tracks_flags_and_language_are_read_from_whatever_ffprobe_wrote(string stream, string codecType, bool isDefault, bool isForced, string? language) =>
        Assert.Equal(
            new ActualOutputTrack(codecType, isDefault, isForced, language),
            Assert.Single(RemuxOutputValidation.ActualLayout(Probe($$"""{"streams":[{{stream}}]}"""))));
}
