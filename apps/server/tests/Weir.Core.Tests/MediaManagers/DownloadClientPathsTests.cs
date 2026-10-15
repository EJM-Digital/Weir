using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary><see cref="DownloadClientPaths"/>: a download client's folder layout, whose text comes from the client and is not trusted.</summary>
public sealed class DownloadClientPathsTests
{
    [Theory]
    [InlineData("/downloads/complete", "tv", "/downloads/complete/tv")]
    [InlineData("/downloads/complete/", "/tv/", "/downloads/complete/tv/")]
    [InlineData("C:\\Downloads", "tv\\shows", "C:\\Downloads\\tv\\shows")]
    [InlineData("/downloads", "a..b/..c/d..", "/downloads/a..b/..c/d..")]
    public void A_relative_folder_is_joined_with_the_separator_the_base_folder_uses(string basePath, string relative, string expected)
    {
        var joined = DownloadClientPaths.Join(basePath, relative);

        Assert.True(joined.Ok);
        Assert.Equal(expected, joined.Path);
        Assert.Null(joined.Problem);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("tv/../../etc")]
    [InlineData("tv\\..\\x")]
    [InlineData("/tv/..")]
    public void A_relative_folder_with_a_parent_segment_is_refused_with_the_reason(string relative)
    {
        var joined = DownloadClientPaths.Join("/downloads/complete", relative);

        Assert.False(joined.Ok);
        Assert.Null(joined.Path);
        Assert.Contains("'..'", joined.Problem, StringComparison.Ordinal);
    }
}
