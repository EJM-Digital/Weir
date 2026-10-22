using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// The folder a media manager is told a file is in, written in the manager's own path style (its host is not necessarily
/// this one): every kind of folder a manager can be set up with.
/// </summary>
public sealed class ManagerPathJoinTests
{
    [Theory]
    [InlineData(@"\\nas\media", @"\\nas\media\Films\A.mkv")]
    [InlineData(@"\\nas\media\Movies", @"\\nas\media\Movies\Films\A.mkv")]
    [InlineData(@"\\nas\media\Movies\2020\", @"\\nas\media\Movies\2020\Films\A.mkv")]
    [InlineData(@"C:\Media", @"C:\Media\Films\A.mkv")]
    [InlineData("c:/Media/Movies/", @"c:\Media\Movies\Films\A.mkv")]
    [InlineData("C:", @"C:Films\A.mkv")]
    [InlineData(@"C:Media", @"C:Media\Films\A.mkv")]
    [InlineData(@"\Media", @"\Media\Films\A.mkv")]
    [InlineData(@"Media\Movies", @"Media\Movies\Films\A.mkv")]
    [InlineData(@"  C:\Media  ", @"C:\Media\Films\A.mkv")]
    public void A_windows_folder_is_joined_with_backslashes_and_keeps_its_drive_or_share(string folder, string expected)
    {
        Assert.Equal(expected, CompletionReports.ManagerPathJoin(folder, ["Films", "A.mkv"]));
    }

    [Theory]
    [InlineData("/media/movies", "/media/movies/Films/A.mkv")]
    [InlineData("/media/movies/", "/media/movies/Films/A.mkv")]
    [InlineData("/", "/Films/A.mkv")]
    [InlineData("//nas/media", "//nas/media/Films/A.mkv")]
    [InlineData("//nas/media/Movies", "//nas/media/Movies/Films/A.mkv")]
    [InlineData("///media", "/media/Films/A.mkv")]
    [InlineData("media/movies", "media/movies/Films/A.mkv")]
    [InlineData("  /media  ", "/media/Films/A.mkv")]
    public void Any_other_folder_is_joined_with_forward_slashes(string folder, string expected)
    {
        Assert.Equal(expected, CompletionReports.ManagerPathJoin(folder, ["Films", "A.mkv"]));
    }

    [Theory]
    [InlineData("1:/media")]
    [InlineData("a")]
    public void A_colon_that_does_not_follow_a_drive_letter_is_not_a_windows_drive(string folder)
    {
        Assert.DoesNotContain('\\', CompletionReports.ManagerPathJoin(folder, ["A.mkv"]));
    }

    [Fact]
    public void Blank_and_dot_parts_are_dropped_and_nothing_at_all_is_the_current_folder()
    {
        Assert.Equal("/m/a/b", CompletionReports.ManagerPathJoin("/m", ["", ".", "a", "b"]));
        Assert.Equal(@"C:\m\a", CompletionReports.ManagerPathJoin(@"C:\m", [".", "a"]));
        Assert.Equal(".", CompletionReports.ManagerPathJoin(string.Empty, []));
        Assert.Equal(".", CompletionReports.ManagerPathJoin(".", ["."]));
    }
}
