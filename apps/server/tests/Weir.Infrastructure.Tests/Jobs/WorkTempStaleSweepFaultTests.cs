using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A left-behind work file that is locked, refused or gone when the hourly sweep reaches it.</summary>
public sealed class WorkTempStaleSweepFaultTests : IDisposable
{
    private const string StaleName = "Paper.Lanterns.2023.processing.ab12cd34.mkv";
    private const string OtherStaleName = "The.Long.Tide.2024.processing.ef56gh78.mkv";
    private const string TitleSql = "SELECT title FROM activity_events WHERE event_type = 'processing.work_temp_stale_sweep_completed'";

    private readonly StoreFixture _store = new();
    private readonly RecordingLogger<WorkTempStaleSweepHandler> _logger = new();
    private readonly string _movieWork;

    public WorkTempStaleSweepFaultTests()
    {
        _store.Clock.Set(DateTimeOffset.UtcNow);
        _movieWork = ProcessingLibraryFolders.DefaultMovieWorkFolder(_store.Options.WeirHome);
        Directory.CreateDirectory(_movieWork);
    }

    public void Dispose() => _store.Dispose();

    private string StaleFile(string name)
    {
        var path = Path.Join(_movieWork, name);
        System.IO.File.WriteAllText(path, "partial remux output");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(2));
        return path;
    }

    private Task RunAsync(FaultingFileSystem files)
    {
        var handler = new WorkTempStaleSweepHandler(
            new ProcessingJobStore(_store.Database, _store.Clock), _store.Options, _store.Clock, _logger, files);
        return handler.HandleAsync(
            new JobWorkContext(1, PeriodicJobKinds.WorkTempStaleSweep, "{\"media_scope\": \"movie\", \"trigger\": \"scheduled\"}", "test"),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_file_another_program_holds_is_kept_and_the_sweep_says_so()
    {
        var locked = StaleFile(StaleName);
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, pathEndsWith: StaleName);

        await RunAsync(files);

        Assert.True(System.IO.File.Exists(locked));
        Assert.Equal("Work files checked (Movies): 1 could not be removed", await _store.ScalarText(TitleSql));
        Assert.Contains(_logger.At(LogLevel.Warning), message => message.Contains("could not remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_file_that_cannot_be_removed_does_not_stop_the_others()
    {
        var locked = StaleFile(StaleName);
        var other = StaleFile(OtherStaleName);
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.Refused, pathEndsWith: StaleName);

        await RunAsync(files);

        Assert.True(System.IO.File.Exists(locked));
        Assert.False(System.IO.File.Exists(other));
        Assert.Equal("Cleared 1 left-behind work file (Movies); 1 could not be removed", await _store.ScalarText(TitleSql));
    }

    [Fact]
    public async Task A_file_that_vanishes_before_its_age_is_read_is_already_gone_and_not_a_problem()
    {
        var path = StaleFile(StaleName);
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Vanished);

        await RunAsync(files);

        Assert.True(System.IO.File.Exists(path));
        Assert.Equal("Work files checked (Movies): nothing left behind", await _store.ScalarText(TitleSql));
    }

    [Fact]
    public async Task A_work_folder_that_cannot_be_listed_is_logged_and_the_sweep_carries_on()
    {
        var path = StaleFile(StaleName);
        var files = new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused);

        await RunAsync(files);

        Assert.True(System.IO.File.Exists(path));
        Assert.Contains(_logger.At(LogLevel.Warning), message => message.Contains("could not read", StringComparison.Ordinal));
        Assert.Equal("Work files checked (Movies): 1 work folder could not be read", await _store.ScalarText(TitleSql));
    }

    [Fact]
    public async Task Several_files_cleared_are_counted_in_the_plural()
    {
        var first = StaleFile(StaleName);
        var second = StaleFile(OtherStaleName);

        await RunAsync(new FaultingFileSystem());

        Assert.False(System.IO.File.Exists(first));
        Assert.False(System.IO.File.Exists(second));
        Assert.Equal("Cleared 2 left-behind work files (Movies)", await _store.ScalarText(TitleSql));
    }

    [Fact]
    public async Task Several_work_folders_that_cannot_be_listed_are_counted_in_the_plural_and_another_scopes_folder_is_not_one_of_them()
    {
        Directory.CreateDirectory(Path.Join(_store.Home.Path, "own-work"));
        await _store.Execute(
            "INSERT INTO libraries (name, media_type, work_folder) VALUES " +
            $"('Own films', 'movie', '{Path.Join(_store.Home.Path, "own-work").Replace("'", "''", StringComparison.Ordinal)}'), " +
            $"('Own series', 'tv', '{Path.Join(_store.Home.Path, "series-work").Replace("'", "''", StringComparison.Ordinal)}')");

        await RunAsync(new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused).Fail(FileCall.ListFiles, FaultingFileSystem.Refused, onCall: 2));

        Assert.Equal("Work files checked (Movies): 2 work folders could not be read", await _store.ScalarText(TitleSql));
    }
}
