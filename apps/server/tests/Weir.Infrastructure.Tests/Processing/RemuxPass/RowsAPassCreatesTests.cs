using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A pass can be the first thing to record a file: nothing has scanned the watched folder yet. The row it creates holds the size of the
/// file on disk, because the next scan treats a row whose size differs from the file as a file that changed, and starts it again.
/// </summary>
public sealed class RowsAPassCreatesTests : IDisposable
{
    private const string RelativePath = "Film.2024/film.mkv";
    private const int FileBytes = 2345;

    private readonly StoreFixture _store = new();
    private readonly PassFolders _folders = new();
    private readonly LibraryStore _libraries = new();
    private long _libraryId;
    private string _source = string.Empty;

    public void Dispose()
    {
        _folders.Dispose();
        _store.Dispose();
    }

    private async Task SetUpAsync()
    {
        _libraryId = await _store.WithUnitOfWork(async uow => Convert.ToInt64(
            await uow.ExecuteScalarWriteAsync(
                "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
                "VALUES ('Films', 'movie', $watched, $output, $work, 1) RETURNING id",
                ("$watched", _folders.Watched), ("$output", _folders.Output), ("$work", _folders.Work)),
            System.Globalization.CultureInfo.InvariantCulture));
        _source = _folders.Source(RelativePath, FileBytes);
    }

    private Task<long> SizeRecordedAsync() =>
        _store.Scalar($"SELECT size_bytes FROM files WHERE library_id = {_libraryId} AND relative_path = '{RelativePath}'");

    [Fact]
    public async Task A_rejection_that_is_the_first_record_of_a_file_holds_the_files_size()
    {
        await SetUpAsync();

        await _store.WithUnitOfWork(async uow =>
        {
            await RemuxPassFileState.UpsertRejectedAsync(uow, _libraryId, RelativePath, "No audio.", ProcessingFailureClasses.Rules, _source);
            return 0;
        });

        Assert.Equal(FileBytes, await SizeRecordedAsync());
    }

    [Fact]
    public async Task A_failure_that_is_the_first_record_of_a_file_holds_the_files_size()
    {
        await SetUpAsync();

        await _store.WithUnitOfWork(async uow =>
        {
            var library = (await _libraries.GetAsync(uow, _libraryId))!;
            await RemuxPassFileState.RecordFailureAsync(uow, library, RelativePath, ProcessingFailureClasses.Execution, "ffmpeg failed.", DateTimeOffset.UtcNow);
            return 0;
        });

        Assert.Equal(FileBytes, await SizeRecordedAsync());
    }

    [Fact]
    public async Task A_rejection_of_a_file_already_scanned_keeps_the_size_the_scan_recorded()
    {
        await SetUpAsync();
        await _store.Execute($"INSERT INTO files (library_id, relative_path, size_bytes) VALUES ({_libraryId}, '{RelativePath}', 777)");

        await _store.WithUnitOfWork(async uow =>
        {
            await RemuxPassFileState.UpsertRejectedAsync(uow, _libraryId, RelativePath, "No audio.", ProcessingFailureClasses.Rules, _source);
            return 0;
        });

        Assert.Equal(777, await SizeRecordedAsync());
    }
}
