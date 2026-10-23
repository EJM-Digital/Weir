using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>A clean when the operating system refuses a file part-way: its size cannot be read, the staged copy cannot be removed, the list cannot be asked to look again.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    private async Task<(string Path, long JobId)> ChosenFileAsync(long expectedSizeBytes)
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        return (path, await EnqueueChosenCleanAsync(library, path, Keeping(0, 1), expectedSizeBytes));
    }

    [Fact]
    public async Task A_file_whose_size_cannot_be_read_is_not_the_file_that_was_chosen_against()
    {
        var (path, jobId) = await ChosenFileAsync(expectedSizeBytes: 3);
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation, pathEndsWith: "film.mkv");

        await RunCleanAsync(jobId, Handler(files: files));

        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_that_is_gone_when_its_size_is_checked_is_not_the_file_that_was_chosen_against()
    {
        var (path, jobId) = await ChosenFileAsync(expectedSizeBytes: 3);
        var files = new FaultingFileSystem().Before(FileCall.FileExists, _ => File.Delete(path), pathEndsWith: "film.mkv");

        await RunCleanAsync(jobId, Handler(files: files));

        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Empty(_media.Remuxes);
    }

    [Fact]
    public async Task A_staged_copy_that_cannot_be_removed_after_it_could_not_be_moved_is_left_and_the_original_untouched()
    {
        var (_, path, jobId) = await CleanableFileAsync();
        Directory.CreateDirectory(SafeSwapRules.TempPath(path));
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.Refused);

        await RunCleanAsync(jobId, Handler(files: files));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Contains(files.Calls, call => call.Call == FileCall.DeleteFile);
    }

    [Fact]
    public async Task A_matching_file_is_still_skipped_when_the_library_cannot_be_asked_to_scan_again()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await _fixture.Store.Execute(
            $"CREATE TRIGGER refuse_scan BEFORE INSERT ON jobs WHEN NEW.job_kind = '{LibraryModeJobKinds.ScanKind}' BEGIN SELECT RAISE(ABORT, 'scan refused'); END");
        var logger = new ListLogger<LibraryCleanHandler>();

        await HandleAsync(Handler(logger: logger), Payload(library, path));

        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileSkipped));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("could not ask for the library to be scanned again", StringComparison.Ordinal));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{LibraryModeJobKinds.ScanKind}'"));
    }
}
