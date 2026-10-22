using Weir.Core.LibraryMode;
using Weir.Core.Rules;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>What a scan says about a file that is unusual: odd stream data, every kind of change, and sizes the file does not state.</summary>
public sealed class LibraryFilePlannerEdgeTests
{
    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264"}""";

    private const string English = """{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"disposition":{"default":1},"bit_rate":"128000"}""";

    private static ProcessingRulesConfig EnglishOnly() => RemuxRules.DefaultConfig() with
    {
        PrimaryAudioLang = "eng",
        SecondaryAudioLang = string.Empty,
        TertiaryAudioLang = string.Empty,
        AudioPreferenceMode = RemuxRuleValues.PolicyPreferredLangsStrict,
        SubtitleMode = RemuxRuleValues.SubtitleModeKeepAll,
    };

    private static ProbeResult Probe(string format, params string[] streams) =>
        ProbeResult.Parse("{" + format + "\"streams\":[" + string.Join(',', streams) + "]}");

    private static LibraryFilePlanResult Classify(ProbeResult probe, ProcessingRulesConfig? rules = null) =>
        LibraryFilePlanner.Classify(probe, rules ?? EnglishOnly());

    [Fact]
    public void A_file_whose_track_data_cannot_be_read_is_reported_as_unusable()
    {
        var result = Classify(Probe(string.Empty, Video, """{"index":1,"codec_type":5}"""));

        Assert.Equal(LibraryFileClassification.CannotProcess, result.Classification);
        Assert.Equal(LibraryProblemKind.Unreadable, result.ProblemKind);
    }

    [Fact]
    public void A_file_whose_tracks_cannot_be_planned_names_the_reason()
    {
        var result = Classify(Probe(string.Empty, Video, """{"index":1,"codec_type":"audio","codec_name":"aac","channels":"many","tags":{"language":"eng"}}"""));

        Assert.Equal(LibraryFileClassification.CannotProcess, result.Classification);
        Assert.StartsWith("Weir could not plan this file against the workflow's rules: ", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_of_removal_is_named_in_what_would_change()
    {
        var rules = EnglishOnly() with
        {
            SubtitleMode = RemuxRuleValues.SubtitleModeRemoveAll,
            Metadata = new MetadataRules { RemoveImages = true, RemoveAttachments = true, RemoveTitle = true },
        };
        var result = Classify(
            Probe(
                string.Empty,
                Video,
                """{"index":4,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}}""",
                English,
                """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"}}""",
                """{"index":3,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}}""",
                """{"index":5,"codec_type":"attachment","codec_name":"ttf","tags":{"filename":"Arial.ttf"}}"""),
            rules);

        Assert.Equal(LibraryFileClassification.WouldChange, result.Classification);
        Assert.StartsWith("Would remove 1 audio track (jpn", result.Summary, StringComparison.Ordinal);
        Assert.Contains("; remove 1 subtitle track (", result.Summary, StringComparison.Ordinal);
        Assert.EndsWith("; remove 1 embedded image; remove 1 attachment; strip metadata.", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_needs_only_its_tracks_reordered_or_re_tagged_says_so()
    {
        var notDefault = """{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"disposition":{"default":0}}""";

        var result = Classify(Probe(string.Empty, Video, notDefault));

        Assert.Equal(LibraryFileClassification.WouldChange, result.Classification);
        Assert.Equal("Would reorder or re-tag tracks without removing any.", result.Summary);
    }

    [Theory]
    [InlineData("{\"duration\":\"abc\"}")]
    [InlineData("{\"duration\":120}")]
    [InlineData("\"none\"")]
    [InlineData("{}")]
    [InlineData(null)]
    public void A_removed_track_adds_nothing_when_the_file_does_not_say_how_long_it_runs(string? formatJson)
    {
        var removed = """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"bit_rate":"96000"}""";

        var result = Classify(Probe(formatJson is null ? string.Empty : "\"format\":" + formatJson + ",", Video, English, removed));

        Assert.Equal(LibraryFileClassification.WouldChange, result.Classification);
        Assert.Equal(0, result.EstimatedBytesSaved);
    }

    [Theory]
    [InlineData("""{"language":"jpn","BPS":"0"}""", 0)]
    [InlineData("""{"language":"jpn","BPS":"fast"}""", 0)]
    [InlineData("""{"language":"jpn","BPSX":"96000"}""", 0)]
    [InlineData("""{"language":"jpn","BPS-jpn":" 96000 "}""", 1_440_000)]
    [InlineData("""{"language":"jpn","bps":"96000"}""", 1_440_000)]
    [InlineData("""{"language":"jpn","NUMBER_OF_BYTES":"5000"}""", 5_000)]
    public void A_size_tag_is_used_only_when_it_is_a_positive_number_under_the_right_name(string tags, long expected)
    {
        var removed = """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":""" + tags + "}";

        var result = Classify(Probe("\"format\":{\"duration\":\"120.0\"},", Video, English, removed));

        Assert.Equal(expected, result.EstimatedBytesSaved);
    }

    [Fact]
    public void A_removed_track_with_a_bit_rate_that_is_not_positive_adds_nothing()
    {
        var removed = """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"bit_rate":"0"}""";

        Assert.Equal(0, Classify(Probe("\"format\":{\"duration\":\"120.0\"},", Video, English, removed)).EstimatedBytesSaved);
    }
}
