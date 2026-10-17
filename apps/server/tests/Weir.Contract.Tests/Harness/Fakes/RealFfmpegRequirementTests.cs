namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Where the real tools are required (CI), a scenario that needs one fails, naming it, instead of skipping.</summary>
[ContractArea("harness")]
public sealed class RealFfmpegRequirementTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_one_makes_the_tools_required(string? value, bool expected)
    {
        Assert.Equal(expected, RealFfmpeg.IsRequired(value));
    }

    [Fact]
    public void Missing_tools_skip_the_scenario_with_the_reason_that_names_them_when_they_are_optional()
    {
        Assert.Equal(RealFfmpeg.MissingReason, RealFfmpeg.SkipReason(required: false, ffmpegFound: false));
        Assert.Equal(RealFfmpeg.MissingReason, RealFfmpeg.MkvmergeSkipReason(required: false, ffmpegFound: false, mkvmergeFound: true));
        Assert.Equal(RealFfmpeg.MissingMkvmergeReason, RealFfmpeg.MkvmergeSkipReason(required: false, ffmpegFound: true, mkvmergeFound: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Required_tools_never_skip_a_scenario_so_it_runs_and_fails(bool ffmpegFound, bool mkvmergeFound)
    {
        Assert.Null(RealFfmpeg.SkipReason(required: true, ffmpegFound));
        Assert.Null(RealFfmpeg.MkvmergeSkipReason(required: true, ffmpegFound, mkvmergeFound));
    }

    [Fact]
    public void Tools_that_are_found_never_skip_a_scenario()
    {
        Assert.Null(RealFfmpeg.SkipReason(required: false, ffmpegFound: true));
        Assert.Null(RealFfmpeg.MkvmergeSkipReason(required: false, ffmpegFound: true, mkvmergeFound: true));
    }

    [Fact]
    public void Required_tools_that_are_missing_fail_naming_each_one()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealFfmpeg.EnsureAvailable(required: true, ffmpegFound: false, mkvmergeFound: false));

        Assert.Contains(RealFfmpeg.RequireVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(RealFfmpeg.MissingReason, error.Message, StringComparison.Ordinal);
        Assert.Contains(RealFfmpeg.MissingMkvmergeReason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_missing_tool_is_named()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealFfmpeg.EnsureAvailable(required: true, ffmpegFound: true, mkvmergeFound: false));

        Assert.Contains(RealFfmpeg.MissingMkvmergeReason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RealFfmpeg.MissingReason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Nothing_fails_when_the_tools_are_found_or_not_required(bool required, bool ffmpegFound, bool mkvmergeFound)
    {
        RealFfmpeg.EnsureAvailable(required, ffmpegFound, mkvmergeFound);
    }
}
