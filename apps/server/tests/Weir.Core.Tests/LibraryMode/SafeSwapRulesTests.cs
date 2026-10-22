using Weir.Core.LibraryMode;

namespace Weir.Core.Tests.LibraryMode;

public sealed class SafeSwapRulesTests
{
    [Theory]
    [InlineData("/lib/Movie (2020)/Movie (2020).mkv", "/lib/Movie (2020)/Movie (2020).weir-tmp.mkv", "/lib/Movie (2020)/Movie (2020).weir-bak.mkv")]
    [InlineData("/lib/Show/Show.S01E01.1080p.mkv", "/lib/Show/Show.S01E01.1080p.weir-tmp.mkv", "/lib/Show/Show.S01E01.1080p.weir-bak.mkv")]
    [InlineData("/lib/no-extension", "/lib/no-extension.weir-tmp", "/lib/no-extension.weir-bak")]
    [InlineData("/lib/.hidden.mp4", "/lib/.hidden.weir-tmp.mp4", "/lib/.hidden.weir-bak.mp4")]
    [InlineData("relative.mkv", "relative.weir-tmp.mkv", "relative.weir-bak.mkv")]
    public void Temp_and_backup_names_sit_beside_the_original_and_keep_its_extension(string original, string temp, string backup)
    {
        Assert.Equal(temp, SafeSwapRules.TempPath(original));
        Assert.Equal(backup, SafeSwapRules.BackupPath(original));

        Assert.True(SafeSwapRules.TryParseLeftover(temp, out var tempKind, out var fromTemp));
        Assert.Equal((SwapLeftoverKind.Temp, original), (tempKind, fromTemp));
        Assert.True(SafeSwapRules.TryParseLeftover(backup, out var backupKind, out var fromBackup));
        Assert.Equal((SwapLeftoverKind.Backup, original), (backupKind, fromBackup));
    }

    [Theory]
    [InlineData("/lib/Movie.mkv")]
    [InlineData("/lib/Movie.WEIR-TMP.mkv")]
    [InlineData("/lib/Movie.weir-tmp-notes.txt")]
    [InlineData("/lib/Movie.weir-bak.mkv.part")]
    [InlineData("/lib/weir-tmp.mkv")]
    [InlineData("/lib/.weir-tmp")]
    [InlineData("/lib/Movie.weir-tmpx.mkv")]
    public void Only_exact_weir_names_are_leftovers(string path)
    {
        Assert.False(SafeSwapRules.TryParseLeftover(path, out _, out _));
    }

    [Fact]
    public void Free_space_needs_the_file_size_plus_what_the_workflow_keeps_free()
    {
        Assert.Equal(1L << 30, SafeSwapRules.RequiredFreeBytes(0, 1L << 30));
        Assert.Equal((5L << 30) + (2L << 30), SafeSwapRules.RequiredFreeBytes(5L << 30, 2L << 30));
        Assert.Equal(3, SafeSwapRules.RequiredFreeBytes(3, 0));
    }

    [Fact]
    public void In_use_files_wait_5_15_then_60_minutes_and_are_then_reported()
    {
        Assert.Null(SafeSwapRules.InUseRetryDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(5), SafeSwapRules.InUseRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(15), SafeSwapRules.InUseRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(60), SafeSwapRules.InUseRetryDelay(3));
        Assert.Null(SafeSwapRules.InUseRetryDelay(4));
    }

    [Fact]
    public void The_source_changed_message_is_the_one_the_issue_names()
    {
        Assert.Equal("The file changed while Weir was working; nothing was replaced", SafeSwapRules.SourceChangedMessage);
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1023L, "1023 bytes")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L << 30, "5.0 GB")]
    [InlineData(-5L, "0 bytes")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1048575L, "1024.0 KB")]
    [InlineData(1L << 20, "1.0 MB")]
    [InlineData(1L << 40, "1.0 TB")]
    [InlineData(1L << 50, "1024.0 TB")]
    public void Sizes_read_naturally(long bytes, string expected)
    {
        Assert.Equal(expected, SafeSwapRules.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(null, "The cleaned copy did not pass Weir's output checks, so the original was kept.")]
    [InlineData("", "The cleaned copy did not pass Weir's output checks, so the original was kept.")]
    [InlineData("   ", "The cleaned copy did not pass Weir's output checks, so the original was kept.")]
    [InlineData("the output is shorter", "The cleaned copy did not pass Weir's output checks, so the original was kept: the output is shorter")]
    public void A_failed_check_names_the_problem_when_there_is_one(string? problem, string expected)
    {
        Assert.Equal(expected, SafeSwapRules.ValidationFailedMessage(problem));
    }

    [Fact]
    public void A_kept_original_that_could_not_be_moved_names_where_it_was_going_and_that_weir_tries_again()
    {
        Assert.Equal(
            "Weir replaced the file but could not move the original into /originals/Film.mkv; it will try again when it next starts. The system reported: disk full",
            SafeSwapRules.OriginalKeepFailedMessage("/originals/Film.mkv", "disk full"));
    }

    [Fact]
    public void A_leftover_whose_name_starts_with_the_marker_belongs_to_a_file_named_by_its_extension_alone()
    {
        Assert.True(SafeSwapRules.TryParseLeftover("/lib/.weir-tmp.mkv", out var kind, out var original));
        Assert.Equal(SwapLeftoverKind.Temp, kind);
        Assert.Equal("/lib/.mkv", original);
    }

    [Theory]
    [InlineData("/lib/")]
    [InlineData("")]
    public void A_path_with_no_file_name_has_no_temp_or_backup_name(string path)
    {
        var error = Assert.Throws<ArgumentException>(() => SafeSwapRules.TempPath(path));

        Assert.StartsWith("A file path is required.", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SafeSwapRules.BackupPath(path));
    }
}
