namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What "delete" and "keep" say when the workflow a title belonged to is gone or the title's path cannot be a file in its watched folder.</summary>
public sealed partial class HistoryFileRemovalServiceTests
{
    private const string WorkflowGone = "The workflow this file belonged to no longer exists.";

    [Fact]
    public async Task Delete_says_so_when_the_workflow_the_title_belonged_to_is_gone()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        await _fixture.Store.Execute("DELETE FROM libraries");

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(WorkflowGone, outcome.Message);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Keep_says_so_when_the_workflow_the_title_belonged_to_is_gone()
    {
        var library = await LibraryAsync();
        var file = await FileRowAsync(library, "Film/film.mkv", _folders.Source("Film/film.mkv"));
        await _fixture.Store.Execute("DELETE FROM libraries");

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(WorkflowGone, outcome.Message);
    }

    [Fact]
    public async Task Keep_refuses_a_relative_path_that_escapes_the_watched_folder()
    {
        var library = await LibraryAsync();
        var file = await FileRowAsync(library, "../outside.mkv", null);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task A_failed_title_whose_path_escapes_the_watched_folder_does_not_qualify_for_a_choice()
    {
        var library = await LibraryAsync();
        var file = await FileRowAsync(library, "../outside.mkv", null);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.RequiresChoice);
    }
}
