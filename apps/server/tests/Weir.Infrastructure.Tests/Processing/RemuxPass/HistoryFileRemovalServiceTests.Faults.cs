using Weir.Core.Activity;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>The file is held, refused or gone when the remove dialog's delete or keep reaches it.</summary>
public sealed partial class HistoryFileRemovalServiceTests
{
    private const string Film = "Film/film.mkv";

    private HistoryFileRemovalService ServiceOver(IFileSystem files) =>
        new(_fixture.Ports, _fixture.Connections, _fixture.Reporter, new RejectRoutes(_fixture.Ports, _fixture.Reporter), _fixture.Libraries, _fixture.Files, _skipMarkers, files);

    private Task<string?> ActivityTitleAsync(string eventType) =>
        _fixture.Store.ScalarText($"SELECT title FROM activity_events WHERE event_type = '{eventType}'");

    [Fact]
    public async Task Delete_of_a_file_that_is_gone_says_there_is_nothing_to_delete_and_keeps_the_row()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, source);
        File.Delete(source);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal("The file is no longer in the watched folder, so there is nothing to delete.", outcome.Message);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal("film.mkv could not be deleted", await ActivityTitleAsync(ActivityEventTypes.ProcessingFileRemovalDeleted));
    }

    [Fact]
    public async Task Delete_of_a_file_that_cannot_be_read_to_check_it_refuses_and_leaves_the_file_and_the_row()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, source);
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused);

        var outcome = await _fixture.Db(uow => ServiceOver(files).DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file has changed since it failed, so Weir won't delete it. It will be looked at again on the next scan.",
            outcome.Message);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task Delete_of_a_confirmed_file_that_cannot_be_read_to_check_it_asks_for_the_dialog_to_be_reopened()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, null);
        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        var confirmation = new FileRemovalConfirmation(options.UnconfirmedSizeBytes!.Value, options.UnconfirmedModifiedAt!);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation);

        var outcome = await _fixture.Db(uow => ServiceOver(files).DeleteAsync(uow, file, confirmation, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal("This file changed after you opened this dialog. Close it and try again.", outcome.Message);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Delete_by_Weir_alone_of_a_file_another_program_holds_keeps_the_file_and_the_row_and_says_why()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, source);
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);

        var outcome = await _fixture.Db(uow => ServiceOver(files).DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.StartsWith("Weir could not delete the rejected file because it is locked or unavailable", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal("film.mkv could not be deleted", await ActivityTitleAsync(ActivityEventTypes.ProcessingFileRemovalDeleted));
    }

    [Fact]
    public async Task A_dialog_for_a_file_whose_details_cannot_be_read_still_offers_the_choice_without_details()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, null);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished);

        var options = await _fixture.Db(uow => ServiceOver(files).EvaluateAsync(uow, file, CancellationToken.None));

        Assert.True(options.RequiresChoice);
        Assert.Null(options.UnconfirmedSizeBytes);
        Assert.Null(options.UnconfirmedModifiedAt);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Keep_of_a_file_that_cannot_be_read_to_check_it_refuses_and_writes_no_marker()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, source);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Refused);

        var outcome = await _fixture.Db(uow => ServiceOver(files).KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Contains("has changed since it failed", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task Keep_of_a_file_that_is_gone_says_there_is_nothing_to_keep()
    {
        var library = await LibraryAsync();
        var source = _folders.Source(Film);
        var file = await FileRowAsync(library, Film, source);
        File.Delete(source);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal("The file is no longer in the watched folder, so there is nothing to keep.", outcome.Message);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }
}
