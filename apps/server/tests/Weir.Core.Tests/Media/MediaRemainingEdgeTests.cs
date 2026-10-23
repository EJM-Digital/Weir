using System.Text.Json;
using Weir.Core.Media;

namespace Weir.Core.Tests.Media;

/// <summary>The last edges of the media rules: tool output that is empty or the wrong shape, path names with nothing in them, and failures with and without words of their own.</summary>
public sealed class MediaRemainingEdgeTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1]")]
    [InlineData("{broken")]
    public void A_clean_ffprobe_exit_with_output_that_is_not_an_object_is_refused_in_plain_words(string? stdout)
    {
        var error = Assert.Throws<MediaToolException>(() => ProbeOutput.Interpret(0, stdout, null));

        Assert.Equal(ToolFailureText.Generic, error.PlainMessage);
    }

    [Fact]
    public void A_clean_ffprobe_exit_with_an_object_gives_the_object_back()
    {
        Assert.Equal(1, ProbeOutput.Interpret(0, """{"a": 1}""", null).GetProperty("a").GetInt32());
    }

    [Fact]
    public void Probe_output_that_is_not_an_object_has_no_duration_and_no_streams_to_validate()
    {
        Assert.Null(ProbeOutput.DurationSeconds(Parse("[1]")));
        Assert.Throws<MediaToolException>(() => ProbeOutput.ValidateRemuxOutput(Parse("[1]"), expectedAudio: 1));
    }

    [Fact]
    public void An_integrity_failure_with_no_error_text_says_the_data_was_incomplete()
    {
        var error = ProbeOutput.IntegrityFailure(null);

        Assert.Contains("incomplete media data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_is_read_from_out_time_lines_and_everything_else_is_skipped()
    {
        var progress = ProbeOutput.LastProgressOutTimeSeconds("no equals sign here\nframe=3\nout_time_ms=abc\nout_time_ms=2500000\nspeed=2x\n");

        Assert.Equal(2.5, progress);
        Assert.Null(ProbeOutput.LastProgressOutTimeSeconds("out_time_ms=n/a\nframe=1"));
    }

    [Fact]
    public void A_remux_into_another_container_names_the_container_it_produced()
    {
        var plan = new Weir.Core.Rules.RemuxPlan { VideoIndices = [], Audio = [], Subtitles = [] };
        var output = Parse("""{"format": {"format_name": "mov,mp4,m4a,3gp,3g2,mj2"}, "streams": []}""");

        var error = Assert.ThrowsAny<MediaToolException>(() => RemuxOutputValidation.ValidateAgainstPlan(output, plan, "matroska,webm", 60, [], []));

        Assert.Contains("(mov,mp4,m4a,3gp,3g2,mj2)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "best")]
    [InlineData("", "best")]
    [InlineData(" FFmpeg ", "ffmpeg")]
    [InlineData("other", "best")]
    public void A_remux_writer_choice_that_is_missing_or_unknown_reads_as_best(string? value, string expected)
    {
        Assert.Equal(expected, RemuxWriterChoice.Normalize(value));
    }

    [Fact]
    public void A_failure_is_put_in_plain_words_by_what_it_carries_or_what_it_is()
    {
        Assert.Equal("Said plainly", ToolFailureText.Plain(new MediaToolException("raw") { PlainMessage = "Said plainly" }));
        Assert.Equal("raw", ToolFailureText.Plain(new MediaToolException("raw") { PlainMessage = string.Empty }));
        Assert.Equal(ToolFailureText.UnreadableFile, ToolFailureText.Plain(new MediaUnreadableException("raw")));
        Assert.Equal(ToolFailureText.TookTooLong, ToolFailureText.Plain(new MediaToolTimeoutException("raw")));
        Assert.Equal("elsewhere", ToolFailureText.Plain(new InvalidOperationException("elsewhere")));
    }

    [Fact]
    public void Only_a_failure_the_tools_explain_has_a_known_reason()
    {
        Assert.Equal("Said plainly", ToolFailureText.KnownReason(new MediaToolException("raw") { PlainMessage = "Said plainly" }));
        Assert.Equal(ToolFailureText.UnreadableFile, ToolFailureText.KnownReason(new MediaUnreadableException("raw")));
        Assert.Equal(ToolFailureText.TookTooLong, ToolFailureText.KnownReason(new MediaToolTimeoutException("raw")));
        Assert.Null(ToolFailureText.KnownReason(new MediaToolException("raw")));
        Assert.Null(ToolFailureText.KnownReason(new MediaToolException("raw") { PlainMessage = string.Empty }));
        Assert.Null(ToolFailureText.KnownReason(new InvalidOperationException("elsewhere")));
    }

    [Theory]
    [InlineData("a/b/name.", "name.")]
    [InlineData("a/b/.hidden", ".hidden")]
    [InlineData("a/b/film.mkv", "film")]
    public void A_name_with_a_leading_or_trailing_dot_keeps_its_stem(string path, string stem)
    {
        Assert.Equal(stem, MediaPathNames.Stem(path, windows: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_lone_dot_has_no_name(bool windows)
    {
        Assert.Equal(string.Empty, MediaPathNames.Name(".", windows));
    }

    [Theory]
    [InlineData("a\r", 1)]
    [InlineData("a\rb", 2)]
    [InlineData("a\r\nb", 2)]
    public void A_carriage_return_ends_a_line_with_or_without_a_line_feed_after_it(string text, int lines)
    {
        Assert.Equal(lines, MediaText.SplitLines(text).Count);
    }

    [Fact]
    public void A_number_formatted_to_no_decimals_has_no_decimal_point()
    {
        Assert.Equal("2", MediaText.FormatFixed(2.5, 0));
        Assert.Equal("0.5", MediaText.FormatFixed(0.5, 1));
    }

    [Fact]
    public void A_hardware_acceleration_listing_with_no_output_names_no_methods()
    {
        var report = HardwareAcceleration.ReportFromHwaccels(0, null);

        Assert.True(report.Detected);
        Assert.Empty(report.AvailableMethods);
    }

    [Theory]
    [InlineData("""{"tracks": "none"}""")]
    [InlineData("""{"attachments": {}}""")]
    [InlineData("""{"tracks": [{"id": "x"}, 3]}""")]
    public void Mkvmerge_identification_ignores_tracks_and_attachments_that_are_not_lists_of_objects(string json)
    {
        var identification = MkvmergeCommands.ParseIdentification(Parse(json));

        Assert.Empty(identification.Tracks);
        Assert.Empty(identification.Attachments);
    }
}
