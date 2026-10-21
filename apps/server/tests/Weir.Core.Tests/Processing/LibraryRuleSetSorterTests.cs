using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>A rule set whose track order cannot be read is refused with the reason, for audio and for subtitles.</summary>
public sealed class LibraryRuleSetSorterTests
{
    private static readonly ProcessingRuleSetRecord Row = new() { Name = "Test" };

    [Fact]
    public void An_audio_order_that_is_not_a_list_is_refused()
    {
        var input = new LibraryRules.RuleSetInput { Name = "Test", AudioSortersJson = "{}" };

        var error = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ApplyRuleSetFields(Row, input));

        Assert.Equal("The track sorter list must be a list.", error.Message);
    }

    [Fact]
    public void A_subtitle_order_that_is_not_json_is_refused()
    {
        var input = new LibraryRules.RuleSetInput { Name = "Test", SubtitleSortersJson = "[" };

        var error = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ApplyRuleSetFields(Row, input));

        Assert.Equal("The track sorter list is not valid JSON.", error.Message);
    }
}
