using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Small readings of library mode: how files are sorted, split and compared, and what a scan store reports without a live stream.</summary>
public sealed class LibraryModeSmallBranchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void A_sort_nobody_knows_orders_by_path(string? sort)
    {
        Assert.Equal(LibraryFileSort.Columns[LibraryFileSort.Path], LibraryFileSort.ColumnFor(sort));
        Assert.Equal(LibraryFileSort.Path, LibraryFileSort.Normalize(sort));
    }

    [Theory]
    [InlineData("size", "f.size_bytes")]
    [InlineData("modified", "f.mtime")]
    public void A_sort_weir_knows_orders_by_its_column(string sort, string column)
    {
        Assert.Equal(column, LibraryFileSort.ColumnFor(sort));
        Assert.Equal(sort, LibraryFileSort.Normalize(sort));
        Assert.Equal(LibraryFileSort.Status, LibraryFileSort.Normalize(LibraryFileSort.Status));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData(".mkv, .mp4 ,,.avi", new[] { ".mkv", ".mp4", ".avi" })]
    public void A_list_of_extensions_is_split_on_commas_and_trimmed(string? csv, string[] expected)
    {
        Assert.Equal(expected, LibraryFileWalker.SplitCsv(csv));
    }

    [Theory]
    [InlineData(10UL, 20UL, 100L, 5L, 10UL, 20UL, 100L, 5L, true)]
    [InlineData(10UL, 20UL, 100L, 5L, 11UL, 20UL, 100L, 5L, false)]
    [InlineData(10UL, 20UL, 100L, 5L, 10UL, 21UL, 100L, 5L, false)]
    [InlineData(10UL, 20UL, 100L, 5L, 10UL, 20UL, 101L, 5L, false)]
    [InlineData(10UL, 20UL, 100L, 5L, 10UL, 20UL, 100L, 6L, false)]
    [InlineData(0UL, 0UL, 100L, 5L, 10UL, 20UL, 100L, 5L, true)]
    [InlineData(10UL, 20UL, 100L, 5L, 0UL, 0UL, 100L, 5L, true)]
    public void A_file_is_the_same_file_when_its_size_and_time_match_and_the_device_and_inode_do_when_both_were_read(
        ulong currentDevice, ulong currentInode, long currentSize, long currentTime, ulong recordedDevice, ulong recordedInode, long recordedSize, long recordedTime, bool same)
    {
        var current = new SourceFingerprint(currentDevice, currentInode, currentSize, currentTime);
        var recorded = new SourceFingerprint(recordedDevice, recordedInode, recordedSize, recordedTime);

        Assert.Equal(same, SafeSwap.SameFile(current, recorded));
    }

    [Theory]
    [InlineData("film.mkv", ".")]
    [InlineData("", ".")]
    public void A_path_with_no_folder_is_in_the_current_folder(string path, string folder)
    {
        Assert.Equal(folder, SafeSwap.DirectoryOf(path));
    }

    [Fact]
    public void A_scan_store_with_no_live_stream_has_seen_no_files()
    {
        Assert.Null(new LibraryScanStore().FilesSeen(7));
    }

    [Fact]
    public async Task Removing_a_librarys_scan_history_works_with_no_live_stream_to_tell()
    {
        using var fixture = new StoreFixture();
        var library = await fixture.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        await fixture.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status) VALUES " +
            $"('{LibraryModeJobKinds.ScanDedupeKeyPrefix(library)}1', '{LibraryModeJobKinds.ScanKind}', 'completed')");

        await fixture.WithUnitOfWork(async uow =>
        {
            await new LibrarySettingsStore().DeleteAllForLibraryAsync(uow, library);
            return 0;
        });

        Assert.Equal(0, await fixture.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{LibraryModeJobKinds.ScanKind}'"));
    }
}
