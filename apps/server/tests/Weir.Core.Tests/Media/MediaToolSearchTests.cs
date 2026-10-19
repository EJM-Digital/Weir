using Weir.Core.Media;

namespace Weir.Core.Tests.Media;

/// <summary>Path handling and the PATH search behind <see cref="MediaToolLocations"/>: how names, folders and the home folder are written on each system.</summary>
public sealed class MediaToolSearchTests
{
    [Fact]
    public void The_tool_names_end_in_exe_only_on_windows()
    {
        Assert.Equal(("ffprobe.exe", "ffmpeg.exe"), MediaToolLocations.ToolNames(windows: true));
        Assert.Equal(("ffprobe", "ffmpeg"), MediaToolLocations.ToolNames(windows: false));
        Assert.Equal("mkvmerge.exe", MediaToolLocations.MkvmergeToolName(windows: true));
        Assert.Equal("mkvmerge", MediaToolLocations.MkvmergeToolName(windows: false));
    }

    [Fact]
    public void A_folder_from_the_environment_is_expanded_and_tidied_and_a_blank_one_is_ignored()
    {
        Assert.Equal(
            ["/home/operator/tools/ffmpeg", "/opt/weir/bin/ffmpeg", "/opt/weir/_internal/bin/ffmpeg"],
            MediaToolLocations.CandidateDirectories("  ~/tools//ffmpeg/ ", "/home/operator", "/opt/weir", windows: false));
        Assert.Equal(["/opt/weir/bin/mkvtoolnix", "/opt/weir/_internal/bin/mkvtoolnix"], MediaToolLocations.MkvtoolnixCandidateDirectories("   ", "/home/operator", "/opt/weir", windows: false));
        Assert.Empty(MediaToolLocations.MkvtoolnixCandidateDirectories(null, "/home/operator", null, windows: false));
        Assert.Equal(["/home/operator/mkv"], MediaToolLocations.MkvtoolnixCandidateDirectories("~/mkv", "/home/operator", null, windows: false));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.CandidateDirectories(null, null!, null, windows: false));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.MkvtoolnixCandidateDirectories(null, null!, null, windows: false));
    }

    [Theory]
    [InlineData("~", "/home/operator/")]
    [InlineData("~/tools", "/home/operator/tools")]
    [InlineData("~user/tools", "~user/tools")]
    [InlineData("tools/~", "tools/~")]
    [InlineData("/abs", "/abs")]
    [InlineData(@"~\tools", @"~\tools")]
    public void The_home_folder_replaces_a_leading_tilde_on_linux_only_when_a_slash_follows(string path, string expected) =>
        Assert.Equal(expected, MediaToolLocations.ExpandUser(path, "/home/operator/", windows: false));

    [Theory]
    [InlineData("~", @"C:\Users\operator\")]
    [InlineData("~/tools", @"C:\Users\operator/tools")]
    [InlineData(@"~\tools", @"C:\Users\operator\tools")]
    [InlineData("~user", "~user")]
    public void The_home_folder_replaces_a_leading_tilde_on_windows_for_either_slash(string path, string expected) =>
        Assert.Equal(expected, MediaToolLocations.ExpandUser(path, @"C:\Users\operator\", windows: true));

    [Fact]
    public void A_path_to_expand_and_a_home_folder_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.ExpandUser(null!, "/home", windows: false));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.ExpandUser("~", null!, windows: false));
    }

    [Fact]
    public void Parts_are_joined_with_the_separator_of_the_system_and_an_empty_start_is_skipped()
    {
        Assert.Equal("/opt/weir/bin/ffmpeg", MediaToolLocations.Join(windows: false, "/opt/weir", "bin", "ffmpeg"));
        Assert.Equal(@"C:\app\bin", MediaToolLocations.Join(windows: true, @"C:\app", "bin"));
        Assert.Equal("a/b", MediaToolLocations.Join(windows: false, string.Empty, "a", "b"));
        Assert.Equal("/only", MediaToolLocations.Join(windows: false, "/only"));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.Join(windows: false, null!));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.Join(windows: false, "a", null!));
    }

    [Theory]
    [InlineData("", false, ".")]
    [InlineData(".", false, ".")]
    [InlineData("/", false, "/")]
    [InlineData("/a//b/./c/", false, "/a/b/c")]
    [InlineData("//a/b", false, "//a/b")]
    [InlineData("///a", false, "/a")]
    [InlineData("a/../b", false, "a/../b")]
    [InlineData("rel/dir", false, "rel/dir")]
    [InlineData("", true, ".")]
    [InlineData(@".\.", true, ".")]
    [InlineData(@"C:/a//b/./c\", true, @"C:\a\b\c")]
    [InlineData(@"C:\", true, @"C:\")]
    [InlineData("C:", true, "C:")]
    [InlineData(@"C:rel\x", true, @"C:rel\x")]
    [InlineData(@"\\server\share\x\y", true, @"\\server\share\x\y")]
    [InlineData(@"\\server\share", true, @"\\server\share\")]
    [InlineData(@"\\server", true, @"\\server")]
    [InlineData(@"\\", true, @"\\")]
    [InlineData(@"\a\b", true, @"\a\b")]
    [InlineData("a/b", true, @"a\b")]
    public void A_path_is_tidied_without_asking_the_disk_and_dot_dot_is_kept(string path, bool windows, string expected) =>
        Assert.Equal(expected, MediaToolLocations.Normalize(path, windows));

    [Fact]
    public void A_path_to_tidy_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.Normalize(null!, windows: false));
    }

    [Fact]
    public void The_search_takes_the_first_folder_on_the_path_that_holds_the_tool_and_asks_about_each_folder_once()
    {
        var asked = new List<string>();
        bool Executable(string candidate)
        {
            asked.Add(candidate);
            return candidate == "/b/ffprobe";
        }

        var found = MediaToolLocations.Which("ffprobe", "/a:/b:/a:/c", windows: false, Executable);

        Assert.Equal("/b/ffprobe", found);
        Assert.Equal(["/a/ffprobe", "/b/ffprobe"], asked);
    }

    [Fact]
    public void A_folder_listed_twice_in_different_case_on_windows_is_asked_once()
    {
        var asked = new List<string>();

        var found = MediaToolLocations.Which("ffprobe", @"C:\Tools;c:/tools;D:\More", windows: true, candidate =>
        {
            asked.Add(candidate);
            return false;
        });

        Assert.Null(found);
        Assert.Equal([@"C:\Tools\ffprobe.exe", @"D:\More\ffprobe.exe"], asked);
    }

    [Fact]
    public void A_folder_listed_twice_in_different_case_on_linux_is_two_folders()
    {
        var asked = new List<string>();

        MediaToolLocations.Which("ffprobe", "/Tools:/tools", windows: false, candidate =>
        {
            asked.Add(candidate);
            return false;
        });

        Assert.Equal(["/Tools/ffprobe", "/tools/ffprobe"], asked);
    }

    [Fact]
    public void Without_a_path_the_system_default_is_searched()
    {
        var linux = new List<string>();
        var windows = new List<string>();

        MediaToolLocations.Which("ffprobe", null, windows: false, candidate => { linux.Add(candidate); return false; });
        MediaToolLocations.Which("ffprobe", null, windows: true, candidate => { windows.Add(candidate); return false; });

        Assert.Equal(["/bin/ffprobe", "/usr/bin/ffprobe"], linux);
        Assert.Equal([@"C:\bin\ffprobe.exe"], windows);
        Assert.Null(MediaToolLocations.Which("ffprobe", string.Empty, windows: false, _ => true));
    }

    [Fact]
    public void A_name_that_already_ends_in_exe_is_not_given_a_second_one_on_windows()
    {
        Assert.Equal(@"C:\bin\ffprobe.EXE", MediaToolLocations.Which("ffprobe.EXE", @"C:\bin", windows: true, _ => true));
        Assert.Equal("/bin/tool.exe", MediaToolLocations.Which("tool.exe", "/bin", windows: false, _ => true));
    }

    [Fact]
    public void A_folder_that_already_ends_in_a_separator_or_is_a_bare_drive_gets_no_extra_one()
    {
        Assert.Equal(@"C:\bin\ffprobe.exe", MediaToolLocations.Which("ffprobe", @"C:\bin\", windows: true, _ => true));
        Assert.Equal(@"C:/bin/ffprobe.exe", MediaToolLocations.Which("ffprobe", "C:/bin/", windows: true, _ => true));
        Assert.Equal(@"D:ffprobe.exe", MediaToolLocations.Which("ffprobe", "D:", windows: true, _ => true));
        Assert.Equal("/usr/bin/ffprobe", MediaToolLocations.Which("ffprobe", "/usr/bin/", windows: false, _ => true));
    }

    [Theory]
    [InlineData("/a::/b", false)]
    [InlineData(":/a", false)]
    [InlineData("/a:", false)]
    [InlineData(@"C:\a;;C:\b", true)]
    [InlineData(@";C:\a", true)]
    public void An_empty_entry_on_the_path_is_not_the_current_folder(string path, bool windows)
    {
        var bare = windows ? "ffprobe.exe" : "ffprobe";

        var found = MediaToolLocations.Which("ffprobe", path, windows, candidate => candidate == bare);

        Assert.Null(found);
    }

    [Fact]
    public void The_search_needs_a_command_and_a_way_to_ask()
    {
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.Which(null!, "/bin", windows: false, _ => true));
        Assert.Throws<ArgumentNullException>(() => MediaToolLocations.Which("ffprobe", "/bin", windows: false, null!));
    }
}
