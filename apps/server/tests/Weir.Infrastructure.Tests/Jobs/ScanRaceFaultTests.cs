using Weir.Core.Processing;
using Weir.Core.Security;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.MediaManagers;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A scan whose watched folder or rejected file is held, refused or removed by another program part-way through.</summary>
public sealed class ScanRaceFaultTests : IDisposable
{
    private const string BelowMinimumSizeFile = "Film.2024.mkv";

    private static readonly LibraryStore Libraries = new();
    private static readonly FileStateStore Files = new();

    private readonly StoreFixture _store = new(("WEIR_CREDENTIALS_SECRET", "scan-race-credentials-secret"));
    private readonly ProcessingJobStore _jobs;
    private readonly string _watched;
    private readonly string _output;

    public ScanRaceFaultTests()
    {
        _jobs = new ProcessingJobStore(_store.Database, _store.Clock);
        _watched = _store.Home.Join("watch");
        _output = _store.Home.Join("out");
        Directory.CreateDirectory(_watched);
        Directory.CreateDirectory(_output);
    }

    public void Dispose() => _store.Dispose();

    private async Task<ProcessingWatchedFolderScanDispatchJobHandler> HandlerAsync(IFileSystem files)
    {
        var cipher = new CredentialCipher(_store.Options.CredentialsSecret, _store.Options.SessionSecret, _store.Options.PreviousCredentialsSecrets, _store.Clock);
        var connections = new MediaManagerConnectionService(_store.Options, cipher, new HttpMediaManagerPorts(new FakeManagerHttp()), new MediaManagerConnectionStore());
        await _store.Execute(
            "INSERT INTO operator_settings (id, minimum_free_disk_space_mb) VALUES (1, 0) ON CONFLICT(id) DO UPDATE SET minimum_free_disk_space_mb = 0");
        return new ProcessingWatchedFolderScanDispatchJobHandler(
            _store.Database, _store.Clock, _store.Options, _jobs, connections, new SuiteSettingsStore(new AuthStore()), Libraries, Files, new FileSkipMarkerStore(), fileSystem: files)
        {
            GoneLookAgain = _ => Task.CompletedTask,
        };
    }

    private Task<ProcessingFileRecord?> RecordedAsync(long libraryId, string relativePath) =>
        _store.WithUnitOfWork(uow => Files.FindAsync(uow, libraryId, relativePath), commit: false);

    [Fact]
    public async Task A_rejected_file_another_program_holds_is_kept_and_recorded_as_not_removed()
    {
        var sample = Path.Combine(_watched, BelowMinimumSizeFile);
        await File.WriteAllBytesAsync(sample, [1]);
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(
            _store, _watched, _output, rejectedFileAction: "delete_file", minFileSizeMb: 100);
        var handler = await HandlerAsync(new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, pathEndsWith: BelowMinimumSizeFile));

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, _jobs, libraryId, enqueueRemuxJobs: true);

        Assert.True(File.Exists(sample));
        var recorded = await RecordedAsync(libraryId, BelowMinimumSizeFile);
        Assert.Equal(ProcessingFileStatuses.Skipped, recorded!.Status);
        Assert.Equal(SkipKinds.BelowMinimumSize, recorded.SkipKind);
        Assert.Contains("locked or unavailable", recorded.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_a_pass_already_cleaned_is_not_queued_again_when_the_list_has_lost_its_row()
    {
        var film = Path.Combine(_watched, "Film.2024.mkv");
        await File.WriteAllBytesAsync(film, [1, 2, 3]);
        var copy = Path.Combine(_output, "Film.2024.mkv");
        await File.WriteAllBytesAsync(copy, [1, 2, 3]);
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(_store, _watched, _output);
        var modifiedNs = (File.GetLastWriteTimeUtc(film) - DateTime.UnixEpoch).Ticks * 100;
        await _store.Execute(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, source_size, source_mtime_ns) VALUES " +
            $"({libraryId}, 'Film.2024.mkv', '{copy.Replace("'", "''", StringComparison.Ordinal)}', 3, 1, '2026-10-07 04:00:00.000000', 3, {modifiedNs})");
        var handler = await HandlerAsync(PhysicalFileSystem.Instance);

        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, _jobs, libraryId, enqueueRemuxJobs: true);

        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        var recorded = await RecordedAsync(libraryId, "Film.2024.mkv");
        Assert.Equal(ProcessingFileStatuses.Processed, recorded!.Status);
        Assert.Equal(3, recorded.ProcessedSourceSize);
    }

    [Fact]
    public async Task A_watched_folder_that_cannot_be_read_fails_the_scan_and_changes_no_file_row_and_queues_nothing()
    {
        var film = Path.Combine(_watched, "Film.2024.mkv");
        await File.WriteAllBytesAsync(film, [1]);
        var libraryId = await ProcessingWatchedFolderScanDispatchJobHandlerTests.CreateLibraryAsync(_store, _watched, _output);
        var handlerBefore = await HandlerAsync(PhysicalFileSystem.Instance);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handlerBefore, _jobs, libraryId, enqueueRemuxJobs: true);
        var rowsBefore = await _store.Scalar("SELECT count(*) FROM files");
        var passesBefore = await _store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");
        var handler = await HandlerAsync(new FaultingFileSystem().Fail(FileCall.EnumerateFileSystemInfos, FaultingFileSystem.Refused));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(handler, _jobs, libraryId, enqueueRemuxJobs: true));

        Assert.True(File.Exists(film));
        Assert.Equal(rowsBefore, await _store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal(passesBefore, await _store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM files WHERE status NOT IN ('unprocessed', 'queued', 'processing')"));
    }
}
