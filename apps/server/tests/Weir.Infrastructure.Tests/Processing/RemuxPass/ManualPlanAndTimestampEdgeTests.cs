using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Why a manual plan cannot be loaded for a file, and what Weir says when the original timestamps cannot be carried over.</summary>
public sealed class ManualPlanAndTimestampEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private async Task<ManualPlanEnqueueException> LoadRefusedAsync(string watchedFolder, string relativePath)
    {
        var library = await _fixture.LibraryAsync("movie", watchedFolder);
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{_folders.Output}', work_folder = '{_folders.Work}'");
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status) VALUES ({library}, '{relativePath}', 'unprocessed')");
        var fileId = await _fixture.Store.Scalar("SELECT id FROM files");
        var tools = new MediaTools(new FakeMediaRunner(), new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System);

        return await Assert.ThrowsAsync<ManualPlanEnqueueException>(
            () => _fixture.Db(uow => ManualPlanSupport.LoadAsync(uow, _fixture.Files, _fixture.Libraries, tools, _fixture.Store.Options, fileId, CancellationToken.None)));
    }

    [Fact]
    public async Task A_file_whose_workflow_has_no_watched_folder_cannot_have_a_manual_plan()
    {
        var refused = await LoadRefusedAsync(string.Empty, "Film/film.mkv");

        Assert.Equal(400, refused.StatusCode);
        Assert.Contains("has no watched folder set", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_recorded_outside_its_watched_folder_cannot_have_a_manual_plan()
    {
        var refused = await LoadRefusedAsync(_folders.Watched, "../outside.mkv");

        Assert.Equal(400, refused.StatusCode);
        Assert.Contains("parent segments", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamps_cannot_be_carried_to_an_output_that_is_not_there_and_the_pass_is_not_failed_for_it()
    {
        var source = _folders.Source("film.mkv");

        var problem = SidecarMigration.ApplyOriginalTimestamps(source, _folders.Out("film.mkv"));

        Assert.StartsWith("Weir could not apply the original timestamps to the output (", problem, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }
}
