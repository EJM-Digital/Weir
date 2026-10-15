using Weir.Core.Processing;

namespace Weir.Core.Tests.Processing;

/// <summary>Library folder, name and scope validation (#460, ADR-0014).</summary>
public sealed class LibraryRulesTests
{
    [Fact]
    public void A_library_named_after_an_existing_one_is_refused()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateName("Movies", ["Movies", "TV"]));
        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_name_is_refused() =>
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateName("   ", []));

    [Fact]
    public void An_unknown_media_type_is_refused() =>
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateScope("anime"));

    [Theory]
    [InlineData("movie")]
    [InlineData("MOVIE")]
    [InlineData("tv")]
    [InlineData(" TV ")]
    public void A_known_media_type_normalizes_to_lowercase(string raw) =>
        Assert.Contains(LibraryRules.ValidateScope(raw), ProcessingMediaScopes.All);

    [Fact]
    public void A_watched_folder_with_no_output_folder_is_refused()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateFolders(@"C:\media\watched", null, null, []));
        Assert.Contains("output folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_librarys_own_watched_and_output_folders_may_not_overlap()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateFolders(@"C:\media\shared", null, @"C:\media\shared\output", []));
        Assert.Contains("overlap", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_folders_are_reported_as_overlapping_either_direction()
    {
        Assert.True(LibraryRules.FoldersOverlap("c:/media/a", "c:/media/a/b"));
        Assert.True(LibraryRules.FoldersOverlap("c:/media/a/b", "c:/media/a"));
        Assert.True(LibraryRules.FoldersOverlap("c:/media/a", "c:/media/a"));
        Assert.False(LibraryRules.FoldersOverlap("c:/media/a", "c:/media/b"));
    }

    [Fact]
    public void A_second_librarys_watched_folder_may_not_overlap_the_first()
    {
        var others = new[] { new OtherLibraryFolders(1, "Movies", @"C:\media\movies", @"C:\media\movies-out") };
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateFolders(@"C:\media\movies\extras", @"C:\work", @"C:\media\tv-out", others));
        Assert.Contains("Movies", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_folders_across_libraries_are_accepted()
    {
        var others = new[] { new OtherLibraryFolders(1, "Movies", @"C:\media\movies", @"C:\media\movies-out") };
        Assert.Null(Record.Exception(() => LibraryRules.ValidateFolders(@"C:\media\tv", @"C:\work", @"C:\media\tv-out", others)));
    }

    [Fact]
    public void Empty_folders_never_overlap_anything()
    {
        var others = new[] { new OtherLibraryFolders(1, "Movies", string.Empty, string.Empty) };
        Assert.Null(Record.Exception(() => LibraryRules.ValidateFolders(string.Empty, string.Empty, string.Empty, others)));
    }

    [Theory]
    [InlineData(@"C:\Media\Watched", @"c:/media/watched")]
    [InlineData(@"C:\Media\Watched\", @"c:/media/watched")]
    public void Folder_normalization_is_case_insensitive_and_trims_trailing_separators(string raw, string expected) =>
        Assert.Equal(expected, LibraryRules.NormalizeFolder(raw));

    [Fact]
    public void Manager_connections_are_deduplicated_and_sorted()
    {
        var result = LibraryRules.ValidateManagerConnections([3, 1, 3, 2], new HashSet<long> { 1, 2, 3 });
        Assert.Equal([1L, 2L, 3L], result);
    }

    [Fact]
    public void An_unknown_manager_connection_is_refused()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateManagerConnections([5], new HashSet<long> { 1, 2 }));
        Assert.Contains("5", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_manager_connection_list_needs_no_lookup() =>
        Assert.Empty(LibraryRules.ValidateManagerConnections([], new HashSet<long>()));

    [Fact]
    public void A_rule_set_id_that_does_not_exist_is_refused() =>
        Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateRuleSet(99, exists: false));

    [Fact]
    public void A_null_rule_set_id_is_always_accepted() =>
        Assert.Null(LibraryRules.ValidateRuleSet(null, exists: false));

    // --- folder-safety rules (#723) ---------------------------------------------------------

    [Fact]
    public void A_relative_watched_folder_is_refused()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", "media/watched"));
        Assert.Contains("absolute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_folder_needs_no_validation_at_all() =>
        LibraryRules.ValidateFolderPath("watched", "   ");

    [Fact]
    public void A_folder_with_a_parent_segment_is_refused()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", @"C:\media\..\other"));
        Assert.Contains("..", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData("/")]
    public void A_drive_or_filesystem_root_cannot_be_a_library_folder(string root)
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", root));
        Assert.Contains("root of a drive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_absolute_folder_is_accepted()
    {
        LibraryRules.ValidateFolderPath("watched", @"C:\Media\Movies");
        LibraryRules.ValidateFolderPath("watched", "/media/movies");
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Weir")]
    [InlineData(@"C:\ProgramData\Weir\bin\ffmpeg")]
    public void Weirs_own_data_folder_or_anything_inside_it_cannot_be_a_library_folder(string folder)
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateFolderPath("watched", folder, weirHome: @"C:\ProgramData\Weir"));
        Assert.Contains("data folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_merely_shares_a_prefix_with_weir_home_is_accepted()
    {
        // "C:\ProgramData\Weird" is not inside "C:\ProgramData\Weir" — a naive prefix check without a
        // separator boundary would wrongly refuse it.
        LibraryRules.ValidateFolderPath("watched", @"C:\ProgramData\Weird", weirHome: @"C:\ProgramData\Weir");
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/etc/weir")]
    [InlineData("/usr")]
    [InlineData("/bin")]
    [InlineData("/sbin")]
    [InlineData("/boot")]
    [InlineData("/proc")]
    [InlineData("/sys")]
    [InlineData("/dev")]
    [InlineData("/var/lib")]
    [InlineData("/var/lib/weir")]
    public void A_linux_system_folder_cannot_be_a_library_folder(string folder)
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", folder));
        Assert.Contains("system folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_under_var_but_not_var_lib_is_accepted() =>
        LibraryRules.ValidateFolderPath("watched", "/var/media");

    [Fact]
    public void The_windows_directory_cannot_be_a_library_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var exception = Assert.Throws<ProcessingLibraryException>(() =>
            LibraryRules.ValidateFolderPath("watched", Path.Combine(windowsDir, "System32")));
        Assert.Contains("Windows system folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_files_cannot_be_a_library_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", programFiles));
        Assert.Contains("Windows system folder", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    public void A_workflow_may_ask_for_no_limit_of_its_own_or_up_to_the_total(long requested, long total) =>
        LibraryRules.ValidateMaxConcurrentFiles(requested, total);

    [Fact]
    public void A_workflow_cannot_ask_for_more_at_once_than_the_total_and_is_told_what_to_do()
    {
        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateMaxConcurrentFiles(5, 4));

        Assert.Equal(
            "The most files this workflow runs at once cannot be more than the 4 Weir runs in total. " +
            "Choose a lower number, or raise Files at once in Setup › Performance › Speed first.",
            exception.Message);
    }

    [Fact]
    public void The_whole_user_profile_folder_cannot_be_a_library_folder_but_a_folder_inside_it_can()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // A service account's profile (NETWORK SERVICE, LOCAL SERVICE) lives under the Windows folder, where the
        // system-folder rule already refuses it and everything inside it.
        if (userProfile.StartsWith(windowsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var refused = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", Path.Combine(userProfile, "Downloads")));
            Assert.Contains("Windows system folder", refused.Message, StringComparison.Ordinal);
            return;
        }

        var exception = Assert.Throws<ProcessingLibraryException>(() => LibraryRules.ValidateFolderPath("watched", userProfile));
        Assert.Contains("user profile folder", exception.Message, StringComparison.Ordinal);

        LibraryRules.ValidateFolderPath("watched", Path.Combine(userProfile, "Downloads"));
    }
}
