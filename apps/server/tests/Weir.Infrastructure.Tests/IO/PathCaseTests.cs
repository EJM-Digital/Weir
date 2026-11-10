using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Tests.IO;

/// <summary>Paths are told apart by case on a system that does so, and are one path whatever their case on one that does not.</summary>
public sealed class PathCaseTests
{
    [Fact]
    public void Where_case_counts_two_paths_that_differ_only_in_case_are_different()
    {
        Assert.False(string.Equals("Film/A.mkv", "Film/a.mkv", PathCase.PathComparison(caseSensitive: true)));
        Assert.True(string.Equals("Film/a.mkv", "Film/a.mkv", PathCase.PathComparison(caseSensitive: true)));
        Assert.False(PathCase.PathComparer(caseSensitive: true).Equals("Film/A.mkv", "Film/a.mkv"));
        Assert.True(PathCase.PathComparer(caseSensitive: true).Equals("Film/a.mkv", "Film/a.mkv"));
    }

    [Fact]
    public void Where_case_does_not_count_two_paths_that_differ_only_in_case_are_the_same()
    {
        Assert.True(string.Equals("Film/A.mkv", "Film/a.mkv", PathCase.PathComparison(caseSensitive: false)));
        Assert.False(string.Equals("Film/a.mkv", "Film/b.mkv", PathCase.PathComparison(caseSensitive: false)));
        Assert.True(PathCase.PathComparer(caseSensitive: false).Equals("Film/A.mkv", "Film/a.mkv"));
        Assert.False(PathCase.PathComparer(caseSensitive: false).Equals("Film/a.mkv", "Film/b.mkv"));
    }
}
