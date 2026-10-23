using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A work folder whose saved path cannot be a path, and one that disappears between the check and the listing.</summary>
public sealed class WorkFolderPathEdgeTests : IDisposable
{
    private const string StaleName = "Paper.Lanterns.2023.processing.ab12cd34.mkv";
    private const string TitleSql = "SELECT title FROM activity_events WHERE event_type = 'processing.work_temp_stale_sweep_completed'";

    private readonly StoreFixture _store = new();
    private readonly RecordingLogger<WorkTempStaleSweepHandler> _logger = new();
    private readonly string _movieWork;

    public WorkFolderPathEdgeTests()
    {
        _store.Clock.Set(DateTimeOffset.UtcNow);
        _movieWork = ProcessingLibraryFolders.DefaultMovieWorkFolder(_store.Options.WeirHome);
        Directory.CreateDirectory(_movieWork);
    }

    public void Dispose() => _store.Dispose();

    private string StaleFile()
    {
        var path = Path.Join(_movieWork, StaleName);
        File.WriteAllText(path, "partial remux output");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(2));
        return path;
    }

    private Task SweepAsync(FaultingFileSystem? files = null) =>
        new WorkTempStaleSweepHandler(new ProcessingJobStore(_store.Database, _store.Clock), _store.Options, _store.Clock, _logger, files ?? new FaultingFileSystem())
            .HandleAsync(new JobWorkContext(1, PeriodicJobKinds.WorkTempStaleSweep, "{\"media_scope\": \"movie\"}", "test"), CancellationToken.None);

    [Fact]
    public async Task A_workflow_whose_work_folder_is_not_a_path_is_skipped_and_the_others_are_still_swept()
    {
        var stale = StaleFile();
        await _store.Execute("UPDATE libraries SET work_folder = 'bad' || char(0) || 'folder' WHERE media_type = 'movie'");

        await SweepAsync();

        Assert.False(File.Exists(stale));
        Assert.Equal("Cleared 1 left-behind work file (Movies)", await _store.ScalarText(TitleSql));
    }

    [Fact]
    public async Task A_work_folder_that_goes_between_the_check_and_the_listing_has_nothing_left_behind()
    {
        StaleFile();

        await SweepAsync(new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.FolderVanished));

        Assert.Equal("Work files checked (Movies): nothing left behind", await _store.ScalarText(TitleSql));
        Assert.Empty(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public void An_interrupted_pass_in_a_workflow_whose_work_folder_is_not_a_path_removes_nothing()
    {
        var libraries = new[] { new ProcessingLibraryFolderRow(1, "movie", 0, "bad\0folder", string.Empty) };
        var interrupted = new[] { new InterruptedJob(5, "processing.file.remux_pass.v1", "{\"relative_media_path\":\"Film.mkv\",\"library_id\":1}") };

        var removed = StartupRecovery.RemoveInterruptedRemuxTempFiles(interrupted, libraries, _store.Home.Path, _logger);

        Assert.Equal(0, removed);
    }
}
