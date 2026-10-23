using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Rules;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>The last edges of library mode: tags that are not text, tracks with no index or no usable bit rate, a path with no name, and a rule that gives no value.</summary>
public sealed class LibraryModeRemainingEdgeTests
{
    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264"}""";

    private static ProbeResult Probe(params string[] streams) =>
        ProbeResult.Parse("{\"format\":{\"duration\":\"120.0\"},\"streams\":[" + string.Join(',', streams) + "]}");

    [Theory]
    [InlineData("""{"codec_name":"aac","channels":2,"channel_layout":5}""")]
    [InlineData("""{"codec_name":"aac","channels":2}""")]
    public void An_audio_track_whose_layout_is_not_text_is_described_by_its_channel_count(string stream)
    {
        Assert.Equal("aac stereo", LibraryFileFactsReader.AudioShapeFor(ProbeStreamInfo.Parse(stream)));
    }

    [Theory]
    [InlineData("""{"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"bit_rate":"96000"}""", 1_440_000)]
    [InlineData("""{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"}}""", 0)]
    [InlineData("""{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"bit_rate":"fast"}""", 0)]
    public void A_removed_track_is_sized_by_its_bit_rate_when_it_has_a_usable_one_with_or_without_an_index(string removed, long expected)
    {
        var split = new SplitProbeStreams([ProbeStreamInfo.Parse(Video)], [ProbeStreamInfo.Parse(removed)], []);
        var plan = new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] };

        Assert.Equal(expected, LibraryFilePlanner.EstimateSavings(Probe(Video), split, plan));
    }

    [Fact]
    public void An_originals_destination_for_a_path_with_no_name_is_called_original()
    {
        Assert.Equal("/lib/.weir-originals/original", OriginalsPathPlanner.DestinationPath("/lib", null, ".."));
        Assert.Equal("/lib/.weir-originals/film.mkv", OriginalsPathPlanner.DestinationPath("/lib", null, "/elsewhere/film.mkv"));
    }

    [Fact]
    public void A_language_rule_with_an_except_field_that_has_no_value_does_not_exclude_the_language()
    {
        var json = """
            {"customFormats": [{"id": 1, "name": "x", "specifications": [{
              "implementation": "LanguageSpecification", "negate": false,
              "fields": [{"name": "value", "value": 1}, {"name": "exceptLanguage"}]}]}]}
            """;

        var snapshot = RedownloadRiskDialect.ParseFileFormatSnapshot(WireJsonParser.Parse(json), "radarr");

        Assert.False(snapshot.CustomFormats.Single().Specifications.Single().ExceptLanguage);
    }
}
