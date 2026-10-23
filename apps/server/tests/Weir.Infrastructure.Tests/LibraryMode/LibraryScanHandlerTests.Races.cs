using Weir.Core.LibraryMode;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>A scan when the world changes while it runs, and what it makes of a file that was cleaned before or could not be read before.</summary>
public sealed partial class LibraryScanHandlerTests
{
    [Fact]
    public async Task A_scheduled_scan_whose_schedule_is_turned_off_while_it_runs_queues_no_clean()
    {
        var library = await LibraryAsync();
        await ScheduledLibraryFilesAsync(library);
        var jobId = await _fixture.Store.WithUnitOfWork(async uow =>
            (await _scans.RequestScanAsync(uow, _fixture.Jobs, library, LibraryModeSchedule.Trigger, _fixture.Store.Clock.GetUtcNow())).Id);
        var turnedOff = false;
        _media.OnCallAsync = async () =>
        {
            if (turnedOff)
            {
                return;
            }

            turnedOff = true;
            await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], ScheduleEnabled: false)); return true; });
        };

        await RunScanAsync(jobId);

        Assert.True(turnedOff);
        Assert.Empty(await CleanPayloadsAsync());
        Assert.Equal("Scanned Movies library: 3 files, 2 would change, 0 could not be processed; 0 files queued to be cleaned", (await ScanActivityTitlesAsync())[^1]);
    }

    [Fact]
    public async Task A_file_cleaned_before_is_scanned_against_the_time_it_was_cleaned()
    {
        var library = await LibraryAsync();
        var (changing, _) = await ScheduledLibraryFilesAsync(library);
        await _fixture.Db(async uow => { await _fileMarks.MarkCleanedAsync(uow, library, changing, _fixture.Store.Clock.GetUtcNow()); return true; });

        await RunScanAsync(await EnqueueScanAsync(library));
        await RunScanAsync(await EnqueueScanAsync(library));

        var files = await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false);
        Assert.Equal(LibraryFileClassification.WouldChange, files.Single(file => file.Path == changing).Classification);
    }

    [Fact]
    public async Task A_file_that_could_not_be_read_is_probed_again_by_the_next_scan()
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await File.WriteAllBytesAsync(_libraryFolder.Join("film.mkv"), [1, 2, 3]);
        _media.ProbeError = "moov atom not found";

        await RunScanAsync(await EnqueueScanAsync(library));
        var probesAfterFirst = _media.Probed.Count();
        await RunScanAsync(await EnqueueScanAsync(library));

        Assert.True(_media.Probed.Count() > probesAfterFirst);
        var file = Assert.Single(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
        Assert.Equal(LibraryFileClassification.CannotProcess, file.Classification);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_manager_that_lists_no_files_matches_nothing_and_is_not_an_error(bool libraryHoldsFiles)
    {
        var library = await LibraryAsync();
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });
        await _fixture.AddConnectionAsync("radarr");
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/movie", "[]");
        if (libraryHoldsFiles)
        {
            await File.WriteAllBytesAsync(_libraryFolder.Join("film.mkv"), [1, 2, 3]);
            _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;
        }

        await RunScanAsync(await EnqueueScanAsync(library));

        var outcome = await _fixture.Db(async uow => await _scans.OutcomeAsync(uow, library, await _scans.LatestAsync(uow, library), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), commit: false);
        Assert.Empty(outcome!.Errors);
        Assert.All(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false), file => Assert.Null(file.ManagerKind));
    }
}
