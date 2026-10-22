using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>A clean that has committed is never undone by the notes taken after it: when one cannot be written, the file stays cleaned and the log says which.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    private async Task<(long Library, string Path, long JobId)> CleanableFileAsync()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        return (library, path, await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));
    }

    private static bool Logged(ListLogger<LibraryCleanHandler> logger, string text) =>
        logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task A_clean_stays_cleaned_when_the_cleaned_mark_cannot_be_saved_and_no_rescan_is_asked_for()
    {
        var (_, path, jobId) = await CleanableFileAsync();
        await _fixture.Store.Execute("CREATE TRIGGER refuse_marks BEFORE INSERT ON library_file_marks BEGIN SELECT RAISE(ABORT, 'marks refused'); END");
        var logger = new ListLogger<LibraryCleanHandler>();

        await RunCleanAsync(jobId, Handler(logger: logger));

        Assert.NotEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileCleaned));
        Assert.True(Logged(logger, "recording that it was cleaned failed"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{LibraryModeJobKinds.ScanKind}'"));
    }

    [Fact]
    public async Task A_clean_stays_cleaned_when_the_resolution_cannot_be_counted_against_the_budget()
    {
        var (_, path, jobId) = await CleanableFileAsync();
        await _fixture.Store.Execute("UPDATE jobs SET runner_cost = 99");
        await _fixture.Store.Execute(
            "CREATE TRIGGER refuse_cost BEFORE UPDATE OF runner_cost ON jobs WHEN OLD.status = 'leased' BEGIN SELECT RAISE(ABORT, 'cost refused'); END");
        var logger = new ListLogger<LibraryCleanHandler>();

        await RunCleanAsync(jobId, Handler(logger: logger));

        Assert.True(Logged(logger, "could not record the file's resolution against the budget"));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileCleaned));
    }

    [Fact]
    public async Task A_clean_stays_cleaned_when_its_activity_entry_cannot_be_written()
    {
        var (_, path, jobId) = await CleanableFileAsync();
        await _fixture.Store.Execute("CREATE TRIGGER refuse_activity BEFORE INSERT ON activity_events BEGIN SELECT RAISE(ABORT, 'activity refused'); END");
        var logger = new ListLogger<LibraryCleanHandler>();

        await RunCleanAsync(jobId, Handler(logger: logger));

        Assert.NotEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.True(Logged(logger, "could not record its activity entry"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public async Task A_file_that_cannot_be_moved_into_place_leaves_no_staged_copy_behind_and_the_original_untouched()
    {
        var (_, path, jobId) = await CleanableFileAsync();
        Directory.CreateDirectory(SafeSwapRules.TempPath(path));

        await RunCleanAsync(jobId);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(_libraryFolder.Path));
    }
}
