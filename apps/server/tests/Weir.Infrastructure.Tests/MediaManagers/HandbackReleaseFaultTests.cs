using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The hand-back copy is held, refused or moved by the manager at the moment Weir goes to remove it.</summary>
public sealed class HandbackReleaseFaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _output;
    private readonly string _watched;

    public HandbackReleaseFaultTests()
    {
        _output = _dir.Join("output");
        _watched = _dir.Join("watched");
        Directory.CreateDirectory(_output);
        Directory.CreateDirectory(_watched);
    }

    public void Dispose() => _dir.Dispose();

    private string Copy()
    {
        var path = Path.Combine(_output, "Film.mkv");
        File.WriteAllText(path, "film");
        return path;
    }

    private HandbackRow RowFor(string copy)
    {
        HandbackStore.TryMeasure(copy, out var size, out var mtimeNs);
        return new HandbackRow(1, 1, "Film.mkv", copy, size, mtimeNs, null, null, null, null, null, null, null, null, null, _watched, _output, "movie");
    }

    [Fact]
    public void A_copy_another_program_holds_is_kept_and_the_note_says_it_can_be_removed_by_hand()
    {
        var copy = Copy();
        var row = RowFor(copy);
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);

        var release = HandbackStore.Release(row, "Deluno", files: files);

        Assert.Equal(HandbackReleaseKind.InUse, release.Kind);
        Assert.StartsWith("Weir could not remove its copy (", release.Note, StringComparison.Ordinal);
        Assert.EndsWith("remove it by hand if you want the space back.", release.Note, StringComparison.Ordinal);
        Assert.Equal("film", File.ReadAllText(copy));
    }

    [Fact]
    public void A_copy_the_system_refuses_to_read_is_kept_and_reported_as_in_use()
    {
        var copy = Copy();
        var row = RowFor(copy);
        var files = new FaultingFileSystem().Fail(FileCall.GetAttributes, FaultingFileSystem.Refused);

        var release = HandbackStore.Release(row, "Deluno", files: files);

        Assert.Equal(HandbackReleaseKind.InUse, release.Kind);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_copy_the_manager_moves_away_while_it_is_checked_is_already_gone_and_not_called_in_use()
    {
        var copy = Copy();
        var row = RowFor(copy);
        var files = new FaultingFileSystem().Before(FileCall.GetLength, File.Delete);

        var release = HandbackStore.Release(row, "Deluno", files: files);

        Assert.Equal(HandbackReleaseKind.AlreadyGone, release.Kind);
        Assert.Equal(HandbackRules.AlreadyGoneNote("Deluno"), release.Note);
    }

    [Fact]
    public void A_copy_whose_folder_goes_while_it_is_deleted_is_already_gone()
    {
        var copy = Copy();
        var row = RowFor(copy);
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.FolderVanished);

        var release = HandbackStore.Release(row, "Deluno", files: files);

        Assert.Equal(HandbackReleaseKind.AlreadyGone, release.Kind);
    }

    [Fact]
    public void A_copy_whose_size_is_not_what_Weir_wrote_is_kept_and_the_note_says_it_changed()
    {
        var copy = Copy();
        var row = RowFor(copy);
        var release = HandbackStore.Release(row with { OutputSize = row.OutputSize + 1 }, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.ChangedNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_copy_that_has_become_a_folder_is_kept_and_the_note_says_it_changed()
    {
        var copy = Copy();
        var row = RowFor(copy);
        File.Delete(copy);
        Directory.CreateDirectory(copy);

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.ChangedNote, release.Note);
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public void A_copy_that_is_not_there_is_already_gone()
    {
        var copy = Copy();
        var row = RowFor(copy);
        File.Delete(copy);

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.AlreadyGone, release.Kind);
    }

    [Fact]
    public void A_copy_with_no_recorded_output_folder_is_kept_as_outside_the_output_folder()
    {
        var copy = Copy();
        var row = RowFor(copy) with { OutputFolder = " " };

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.OutsideNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_copy_with_a_path_the_system_cannot_resolve_is_kept_as_outside_the_output_folder()
    {
        var copy = Copy();
        var row = RowFor(copy) with { OutputPath = "bad\0path" };

        var release = HandbackStore.Release(row, "Deluno");

        Assert.Equal(HandbackReleaseKind.Kept, release.Kind);
        Assert.Equal(HandbackRules.OutsideNote, release.Note);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void A_copy_whose_size_cannot_be_read_is_not_measured_for_the_hand_back()
    {
        var copy = Copy();
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation);

        Assert.False(HandbackStore.TryMeasure(copy, out var size, out var mtimeNs, files));
        Assert.Equal(0, size);
        Assert.Equal(0, mtimeNs);
        Assert.False(HandbackStore.TryMeasure(Path.Combine(_output, "Gone.mkv"), out _, out _));
    }
}
