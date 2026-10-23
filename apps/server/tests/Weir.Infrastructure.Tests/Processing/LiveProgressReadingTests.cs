using Weir.Core.Json;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>How the live progress reads a report whose fields are missing, blank or of the wrong kind.</summary>
public sealed class LiveProgressReadingTests
{
    private static LiveProgress From(string key, WireValue value) => LiveProgress.FromReport(new WireObject().Set(key, value));

    [Fact]
    public void A_report_with_nothing_in_it_says_the_pass_is_processing_and_knows_nothing_more()
    {
        var progress = LiveProgress.FromReport(new WireObject());

        Assert.Equal("processing", progress.Status);
        Assert.Null(progress.Percent);
        Assert.Null(progress.Message);
        Assert.Null(progress.EtaSeconds);
        Assert.Null(progress.Speed);
        Assert.Null(progress.ElapsedSeconds);
        Assert.Empty(progress.RemovedAudio);
        Assert.Empty(progress.RemovedSubtitles);
    }

    [Fact]
    public void A_percent_is_read_from_a_number_a_flag_or_text_and_kept_between_nought_and_a_hundred()
    {
        Assert.Equal(1.0, From("percent", WireBool.True).Percent);
        Assert.Equal(0.0, From("percent", WireBool.False).Percent);
        Assert.Equal(42.5, From("percent", new WireString("42.5")).Percent);
        Assert.Equal(100.0, From("percent", new WireInteger(250)).Percent);
        Assert.Equal(0.0, From("percent", new WireNumber(-3.5)).Percent);
        Assert.Null(From("percent", new WireString("soon")).Percent);
        Assert.Null(From("percent", new WireNumber(double.NaN)).Percent);
        Assert.Null(From("percent", WireNull.Instance).Percent);
    }

    [Fact]
    public void A_time_left_or_elapsed_is_a_number_of_seconds_that_is_not_negative()
    {
        Assert.Equal(90.0, From("eta_seconds", new WireInteger(90)).EtaSeconds);
        Assert.Equal(1.5, From("elapsed_seconds", new WireNumber(1.5)).ElapsedSeconds);
        Assert.Null(From("eta_seconds", new WireInteger(-1)).EtaSeconds);
        Assert.Null(From("elapsed_seconds", new WireNumber(double.NaN)).ElapsedSeconds);
    }

    [Theory]
    [InlineData("finishing", "finishing")]
    [InlineData("  FINISHING ", "finishing")]
    [InlineData("   ", "processing")]
    [InlineData("", "processing")]
    public void The_status_is_the_trimmed_lower_case_word_or_processing_when_there_is_none(string given, string expected)
    {
        Assert.Equal(expected, From("status", new WireString(given)).Status);
    }

    [Fact]
    public void A_status_that_is_not_text_is_processing()
    {
        Assert.Equal("processing", From("status", new WireInteger(5)).Status);
    }

    [Fact]
    public void A_message_is_trimmed_and_a_blank_or_non_text_one_is_none()
    {
        Assert.Equal("Writing the file", From("message", new WireString("  Writing the file ")).Message);
        Assert.Null(From("message", new WireString("   ")).Message);
        Assert.Null(From("message", new WireInteger(5)).Message);
    }

    [Fact]
    public void The_removed_tracks_are_the_non_blank_texts_of_a_list_and_nothing_when_it_is_not_a_list()
    {
        var progress = LiveProgress.FromReport(new WireObject()
            .Set("removed_audio", new WireArray([new WireString("English AC3"), new WireInteger(3), new WireString("  ")]))
            .Set("removed_subtitles", new WireString("not a list")));

        Assert.Equal(["English AC3"], progress.RemovedAudio);
        Assert.Empty(progress.RemovedSubtitles);
    }

    [Fact]
    public void A_byte_count_is_a_whole_number_that_is_not_negative_whatever_it_was_written_as()
    {
        Assert.Equal(12, From("bytes_read", new WireString("12")).BytesRead);
        Assert.Equal(12, From("bytes_written", new WireNumber(12.9)).BytesWritten);
        Assert.Null(From("bytes_read", new WireNumber(double.NaN)).BytesRead);
        Assert.Null(From("bytes_written", new WireNumber(double.PositiveInfinity)).BytesWritten);
        Assert.Null(From("bytes_read", new WireString("lots")).BytesRead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("12.5 MB/s")]
    [InlineData("N/A")]
    [InlineData("-2x")]
    [InlineData("NaNx")]
    [InlineData("Infinityx")]
    public void Speed_text_that_is_not_a_multiple_has_no_multiple(string? speed)
    {
        Assert.Null(PassSpeed.Multiple(speed));
    }
}
