using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>When Weir removes a copy it handed back, and when it leaves it alone, against real folders and files.</summary>
public sealed class HandbackReleaseTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _output;
    private readonly string _watched;

    public HandbackReleaseTests()
    {
        _output = _dir.Join("output");
        _watched = _dir.Join("watched");
        Directory.CreateDirectory(_output);
        Directory.CreateDirectory(_watched);
    }

    public void Dispose() => _dir.Dispose();

    private HandbackRow RowFor(string copy, string? outputFolder = null, string? watchedFolder = null)
    {
        HandbackStore.TryMeasure(copy, out var size, out var mtimeNs);
        return new HandbackRow(
            1, 1, "Film.mkv", copy, size, mtimeNs, null, null, null, null, null, null, null, null, null,
            watchedFolder ?? _watched, outputFolder ?? _output, "movie");
    }

    private string Copy(string name = "Film.mkv", string content = "film")
    {
        var path = Path.Combine(_output, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_copy_that_is_still_exactly_what_Weir_wrote_is_removed()
    {
        var copy = Copy();

        var release = HandbackStore.Release(RowFor(copy), "Deluno");

        Assert.Equal(HandbackReleaseKind.Removed, release.Kind);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void A_copy_that_changed_since_it_was_written_is_left_alone()
    {
        var copy = Copy();
        var row = RowFor(copy);
        File.WriteAllText(copy, "film, edited since");

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.ChangedNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_copy_that_is_gone_was_moved_by_the_manager_and_nothing_is_removed()
    {
        var copy = Copy();
        var row = RowFor(copy);
        File.Delete(copy);

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.AlreadyGone, release.Kind);
        Assert.True(release.Settles);
        Assert.False(release.Removed);
    }

    [Fact]
    public void A_folder_where_the_copy_was_is_left_alone()
    {
        var copy = Path.Combine(_output, "Film.mkv");
        var row = RowFor(copy);
        Directory.CreateDirectory(copy);

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.ChangedNote, release.Note);
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public void The_managers_own_library_file_is_never_removed()
    {
        var copy = Copy();

        var release = HandbackStore.Release(RowFor(copy), "Deluno", libraryFilePath: copy);

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.SameAsLibraryNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_different_library_file_does_not_hold_the_copy_back()
    {
        var copy = Copy();

        var release = HandbackStore.Release(RowFor(copy), "Deluno", libraryFilePath: Path.Combine(_dir.Join("library"), "Film.mkv"));

        Assert.Equal(HandbackReleaseKind.Removed, release.Kind);
    }

    [Fact]
    public void A_copy_outside_the_output_folder_is_never_removed()
    {
        var elsewhere = _dir.Join("elsewhere.mkv");
        File.WriteAllText(elsewhere, "film");

        var release = HandbackStore.Release(RowFor(elsewhere), "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.OutsideNote, release.Note);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void A_copy_inside_the_watched_folder_is_never_removed_even_when_that_is_under_the_output_folder()
    {
        var watched = Path.Combine(_output, "downloads");
        Directory.CreateDirectory(watched);
        var inDownloads = Path.Combine(watched, "Film.mkv");
        File.WriteAllText(inDownloads, "film");

        var release = HandbackStore.Release(RowFor(inDownloads, watchedFolder: watched), "Deluno");

        Assert.Equal(HandbackRules.OutsideNote, release.Note);
        Assert.True(File.Exists(inDownloads));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_workflow_with_no_output_folder_has_nothing_to_remove_from(string outputFolder)
    {
        var copy = Copy();

        var release = HandbackStore.Release(RowFor(copy, outputFolder: outputFolder), "Deluno");

        Assert.Equal(HandbackRules.OutsideNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_path_that_cannot_be_read_is_treated_as_outside()
    {
        var copy = Copy();

        var release = HandbackStore.Release(RowFor(copy) with { OutputPath = "bad\0path" }, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.OutsideNote, release.Note);
    }

    [Fact]
    public void The_parts_of_a_copys_path_run_from_the_output_folder_to_the_file()
    {
        var copy = Path.Combine(_output, "Films", "A", "Film.mkv");

        Assert.Equal(["Films", "A", "Film.mkv"], HandbackStore.RelativeParts(RowFor(copy)));
    }

    [Fact]
    public void A_copy_not_under_the_output_folder_or_a_workflow_without_one_has_no_parts()
    {
        Assert.Empty(HandbackStore.RelativeParts(RowFor(_dir.Join("elsewhere", "Film.mkv"))));
        Assert.Empty(HandbackStore.RelativeParts(RowFor(Path.Combine(_output, "Film.mkv"), outputFolder: " ")));
        Assert.Empty(HandbackStore.RelativeParts(RowFor(Path.Combine(_output, "Film.mkv")) with { OutputPath = "bad\0path" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0path")]
    [InlineData("missing.mkv")]
    public void A_file_that_cannot_be_measured_is_not_measured(string name)
    {
        var path = name is "missing.mkv" ? _dir.Join(name) : name;

        Assert.False(HandbackStore.TryMeasure(path, out var size, out var mtimeNs));
        Assert.Equal(0, size);
        Assert.Equal(0, mtimeNs);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_measured_is_not_recorded_so_it_can_never_be_removed()
    {
        using var store = new StoreFixture();
        var handbacks = new HandbackStore();
        var libraryId = await store.Scalar("SELECT id FROM libraries ORDER BY id LIMIT 1");

        await store.WithUnitOfWork(async uow =>
        {
            await handbacks.RecordWrittenAsync(uow, libraryId, "Gone.mkv", _dir.Join("gone.mkv"), DateTimeOffset.UnixEpoch);
            await handbacks.RecordWrittenAsync(uow, libraryId, "Blank.mkv", "  ", DateTimeOffset.UnixEpoch);
            return 0;
        });

        Assert.Equal(0, await store.Scalar("SELECT count(*) FROM handbacks"));
    }
}
