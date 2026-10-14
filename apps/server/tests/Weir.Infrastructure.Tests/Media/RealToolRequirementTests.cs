namespace Weir.Infrastructure.Tests.Media;

public sealed class RealToolRequirementTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    public void Only_a_value_of_1_requires_the_real_tools(string? value, bool expected) =>
        Assert.Equal(expected, RealToolRequirement.IsRequired(value));

    [Fact]
    public void A_missing_ffmpeg_skips_a_real_ffmpeg_test_where_the_tools_are_optional()
    {
        var reason = RealToolRequirement.FfmpegSkipReason(required: false, ffmpegFound: false);

        Assert.Equal(RealToolRequirement.FfmpegMissing, reason);
    }

    [Fact]
    public void A_missing_ffmpeg_does_not_skip_a_real_ffmpeg_test_where_the_tools_are_required()
    {
        Assert.Null(RealToolRequirement.FfmpegSkipReason(required: true, ffmpegFound: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_found_ffmpeg_never_skips_a_real_ffmpeg_test(bool required)
    {
        Assert.Null(RealToolRequirement.FfmpegSkipReason(required, ffmpegFound: true));
    }

    [Fact]
    public void A_mkvmerge_test_without_ffmpeg_names_ffmpeg_first_because_it_makes_its_fixtures_with_it()
    {
        var reason = RealToolRequirement.MkvmergeSkipReason(required: false, ffmpegFound: false, mkvmergeFound: false);

        Assert.Equal(RealToolRequirement.FfmpegMissingForMkvmergeFixtures, reason);
    }

    [Fact]
    public void A_mkvmerge_test_with_ffmpeg_but_no_mkvmerge_names_mkvmerge()
    {
        var reason = RealToolRequirement.MkvmergeSkipReason(required: false, ffmpegFound: true, mkvmergeFound: false);

        Assert.Equal(RealToolRequirement.MkvmergeMissing, reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_mkvmerge_test_is_not_skipped_where_the_tools_are_required(bool ffmpegFound, bool mkvmergeFound)
    {
        Assert.Null(RealToolRequirement.MkvmergeSkipReason(required: true, ffmpegFound, mkvmergeFound));
    }

    [Fact]
    public void A_mkvmerge_test_with_both_tools_is_not_skipped()
    {
        Assert.Null(RealToolRequirement.MkvmergeSkipReason(required: false, ffmpegFound: true, mkvmergeFound: true));
    }

    [Fact]
    public void Required_tools_that_are_missing_fail_with_the_message_a_skip_would_have_given()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RealToolRequirement.EnsureAvailable(required: true, ffmpegFound: false, mkvmergeFound: false));

        Assert.Contains(RealToolRequirement.RequireVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(RealToolRequirement.FfmpegMissing, error.Message, StringComparison.Ordinal);
        Assert.Contains(RealToolRequirement.MkvmergeMissing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_missing_tool_is_named()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RealToolRequirement.EnsureAvailable(required: true, ffmpegFound: true, mkvmergeFound: false));

        Assert.Contains(RealToolRequirement.MkvmergeMissing, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RealToolRequirement.FfmpegMissing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Nothing_fails_when_the_tools_are_found_or_not_required(bool required, bool ffmpegFound, bool mkvmergeFound)
    {
        RealToolRequirement.EnsureAvailable(required, ffmpegFound, mkvmergeFound);
    }
}
