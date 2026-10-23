using Microsoft.Extensions.Logging;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// Whether a library's work folder shares a filesystem with its output folder, and the one log line Weir writes at
/// startup when it does not (#716).
/// </summary>
public sealed class WorkFolderPlacementTests
{
    private static readonly LibraryStore Libraries = new();

    private const string MountInfo =
        "22 1 8:1 / / rw,relatime - ext4 /dev/sda1 rw\n" +
        "96 22 0:44 / /data/weir rw,relatime - ext4 /dev/sdb1 rw\n" +
        "97 22 0:45 /srv/media /media rw,relatime - ext4 /dev/sdc1 rw\n" +
        "98 22 0:46 / /mnt/My\\040Disk rw,relatime - ext4 /dev/sdd1 rw\n";

    [Fact]
    public void Mount_points_are_read_from_mountinfo_with_their_spaces_restored()
    {
        Assert.Equal(["/", "/data/weir", "/media", "/mnt/My Disk"], FilesystemBoundaries.ParseMountPoints(MountInfo));
    }

    [Theory]
    [InlineData("/media/weir/work/movies", "/media")]
    [InlineData("/media", "/media")]
    [InlineData("/data/weir/processing/processing-movie-work", "/data/weir")]
    [InlineData("/mediafiles/x", "/")]
    [InlineData("/mnt/My Disk/work", "/mnt/My Disk")]
    public void A_folder_belongs_to_the_deepest_mount_point_it_is_under(string path, string expected)
    {
        Assert.Equal(expected, FilesystemBoundaries.MountPointOf(path, FilesystemBoundaries.ParseMountPoints(MountInfo)));
    }

    [Fact]
    public void Two_folders_under_one_mount_are_on_one_filesystem_and_under_two_they_are_not()
    {
        Assert.True(FilesystemBoundaries.SameMount("/media/a", "/media/b", () => MountInfo));
        Assert.False(FilesystemBoundaries.SameMount("/media/a", "/data/weir/b", () => MountInfo));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_mount_table_that_cannot_be_read_says_nothing(Type failure)
    {
        Assert.Null(FilesystemBoundaries.SameMount("/media/a", "/media/b", () => throw (Exception)Activator.CreateInstance(failure)!));
    }

    [Fact]
    public void A_mount_table_that_lists_no_mounts_says_nothing()
    {
        Assert.Null(FilesystemBoundaries.SameMount("/media/a", "/media/b", () => string.Empty));
    }

    [Fact]
    public void A_mount_line_too_short_to_name_a_mount_point_is_left_out_and_an_escape_that_is_not_octal_is_kept_as_written()
    {
        Assert.Equal(["/mnt/a\\089b"], FilesystemBoundaries.ParseMountPoints("short line\n22 1 8:1 / /mnt/a\\089b rw - ext4 /dev/sda1 rw\n"));
    }

    [Fact]
    public void Two_folders_in_the_same_place_are_on_the_same_filesystem()
    {
        using var temp = new TempDirectory();

        Assert.NotEqual(false, FilesystemBoundaries.SameFilesystem(temp.Join("work"), temp.Join("output")));
    }

    [Fact]
    public async Task A_work_folder_on_another_filesystem_is_reported_once_with_the_advice()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "work-folder-placement-1"));
        await MoviesLibraryAsync(store, store.Home.Join("output"));
        var logger = new ListLogger<WorkFolderPlacementCheck>();
        var check = new WorkFolderPlacementCheck(store.Database, store.Options, Libraries, logger, (_, _) => false);

        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!;

        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("on a different filesystem from its output folder", line.Message, StringComparison.Ordinal);
        Assert.Contains("same volume as the output folder", line.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Nothing_is_said_when_the_folders_share_a_filesystem_or_it_cannot_tell(bool? same)
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "work-folder-placement-2"));
        await MoviesLibraryAsync(store, store.Home.Join("output"));
        var logger = new ListLogger<WorkFolderPlacementCheck>();
        var check = new WorkFolderPlacementCheck(store.Database, store.Options, Libraries, logger, (_, _) => same);

        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!;

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task Workflows_that_cannot_be_read_are_left_unchecked_and_the_server_starts_all_the_same()
    {
        using var store = new StoreFixture(("WEIR_CREDENTIALS_SECRET", "work-folder-placement-3"));
        await store.Execute("ALTER TABLE libraries RENAME TO libraries_unreadable");
        var logger = new ListLogger<WorkFolderPlacementCheck>();
        var check = new WorkFolderPlacementCheck(store.Database, store.Options, Libraries, logger, (_, _) => false);

        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!;

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("could not read the workflows", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    private static async Task MoviesLibraryAsync(StoreFixture store, string output)
    {
        await using var uow = await UnitOfWork.OpenAsync(store.Database);
        var seeded = await Libraries.SeededForScopeAsync(uow, ProcessingMediaScopes.Movie) ?? throw new InvalidOperationException("No seeded Movies library.");
        await Libraries.UpdateAsync(uow, seeded, new ProcessingLibraryInput
        {
            Name = seeded.Name,
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = store.Home.Join("watched"),
            OutputFolder = output,
        });
        await uow.CommitAsync();
    }
}
