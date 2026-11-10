using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// Whether two of a media manager's paths are compared ignoring case follows the manager's own path style, so these cases
/// name a host that is not Windows: on a Windows host every path ignores case, whatever it looks like.
/// </summary>
public sealed class HandbackPathCaseTests
{
    [Theory]
    [InlineData(@"\\nas\media")]
    [InlineData(@"Media\Movies")]
    [InlineData("C:/Media")]
    [InlineData("c:")]
    [InlineData("  Z:/x  ")]
    public void A_path_in_the_windows_style_ignores_case_on_any_host(string path)
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, HandbackRules.Comparison(path, hostIsWindows: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab/c")]
    [InlineData("a/b")]
    [InlineData("1:/media")]
    [InlineData(":/media")]
    [InlineData("/media/movies")]
    public void Any_other_path_keeps_case_on_a_host_that_is_not_windows(string path)
    {
        Assert.Equal(StringComparison.Ordinal, HandbackRules.Comparison(path, hostIsWindows: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/media/movies")]
    [InlineData("1:/media")]
    public void On_a_windows_host_every_path_ignores_case(string path)
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, HandbackRules.Comparison(path, hostIsWindows: true));
    }
}
