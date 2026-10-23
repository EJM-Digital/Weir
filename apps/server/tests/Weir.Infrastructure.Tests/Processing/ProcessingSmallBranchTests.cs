using Weir.Core.Processing;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Small readings in the processing code whose every answer is checked on its own.</summary>
public sealed class ProcessingSmallBranchTests
{
    private static ScannedFileWrite Write(long? rowId, string? expected, string? reset, FileStateVerdict? verdict) =>
        new("a.mkv", rowId, expected, reset, verdict, 10, null);

    [Fact]
    public void A_scan_write_changes_the_status_when_it_adds_the_file_or_names_a_different_status()
    {
        var verdict = new FileStateVerdict(ProcessingFileStatuses.OnHold, "Waiting.");

        Assert.True(Write(null, null, null, new FileStateVerdict(ProcessingFileStatuses.Unprocessed, "New.")).ChangesStatus);
        Assert.True(Write(1, ProcessingFileStatuses.Unprocessed, null, verdict).ChangesStatus);
        Assert.False(Write(1, ProcessingFileStatuses.OnHold, null, verdict).ChangesStatus);
        Assert.True(Write(1, ProcessingFileStatuses.ProcessingFailed, ProcessingFileStatuses.Unprocessed, null).ChangesStatus);
        Assert.False(Write(1, ProcessingFileStatuses.Processed, null, null).ChangesStatus);
    }

    [Theory]
    [InlineData("148x", 148.0)]
    [InlineData("abcx", null)]
    [InlineData("N/Ax", null)]
    [InlineData("x", null)]
    [InlineData("12.5 MB/s", null)]
    [InlineData("-3x", null)]
    [InlineData(null, null)]
    public void A_speed_is_a_multiple_only_when_it_is_a_number_followed_by_an_x(string? speed, double? expected)
    {
        Assert.Equal(expected, PassSpeed.Multiple(speed));
    }

    [Fact]
    public void A_backslash_in_a_mount_point_that_does_not_start_an_octal_escape_is_kept_as_it_is()
    {
        const string MountInfo = "98 22 0:46 / /mnt/a\\!bcd rw,relatime - ext4 /dev/sdd1 rw\n";

        Assert.Equal(["/mnt/a\\!bcd"], FilesystemBoundaries.ParseMountPoints(MountInfo));
    }
}
