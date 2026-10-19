using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>What a scan does with a job that names no workflow, a file it cannot read, and a link count it cannot get.</summary>
public sealed partial class LibraryScanHandlerTests
{
    private sealed class UnreadableLinkCount : IHardlinkInspector
    {
        public int? LinkCount(string path) => throw new UnauthorizedAccessException("denied");
    }

    private async Task<long> EnqueueRawScanAsync(string payload) =>
        (await _fixture.Jobs.EnqueueOrGetAsync("scan:" + Guid.NewGuid().ToString("N"), LibraryModeJobKinds.ScanKind, payload)).Id;

    private async Task<string> PayloadOfAsync(long jobId) =>
        Convert.ToString(await _fixture.Db(uow => uow.ScalarAsync($"SELECT payload_json FROM jobs WHERE id = {jobId}"), commit: false), System.Globalization.CultureInfo.InvariantCulture)!;

    private async Task ScanFolderAsync(long library) =>
        await _fixture.Db(async uow => { await _librarySettings.SetAsync(uow, library, new LibrarySettings([_libraryFolder.Path], false)); return true; });

    [Fact]
    public void The_handler_cannot_be_built_without_any_of_its_parts() =>
        ConstructorGuards.AssertEachRequiredPartIsRefused<LibraryScanHandler>(HandlerArguments);

    [Fact]
    public async Task A_job_with_no_context_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json {")]
    [InlineData("[1,2]")]
    [InlineData("""{"library_id":999}""")]
    public async Task A_job_for_a_workflow_that_is_gone_records_that_and_scans_nothing(string payload)
    {
        var jobId = await EnqueueRawScanAsync(payload);

        await RunScanAsync(jobId);

        var recorded = await PayloadOfAsync(jobId);
        Assert.Contains("\"ok\":false", recorded, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"This workflow no longer exists.\"", recorded, StringComparison.Ordinal);
        Assert.Empty(_media.Calls);
        Assert.Empty(await ScanActivityTitlesAsync());
    }

    [Fact]
    public async Task A_file_the_probe_cannot_read_is_listed_as_not_processable_with_the_reason()
    {
        var library = await LibraryAsync();
        await ScanFolderAsync(library);
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.ProbeError = "moov atom not found";

        await RunScanAsync(await EnqueueScanAsync(library));

        var file = Assert.Single(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
        Assert.Equal(LibraryFileClassification.CannotProcess, file.Classification);
        Assert.False(string.IsNullOrEmpty(file.Reason));
        Assert.Equal(LibraryProblemKind.Unreadable, file.ProblemKind);
        Assert.Equal(["Scanned Movies library: 1 file, 0 would change, 1 could not be processed"], await ScanActivityTitlesAsync());
    }

    [Fact]
    public async Task A_file_the_server_may_not_open_is_listed_as_a_permission_problem()
    {
        var library = await LibraryAsync();
        await ScanFolderAsync(library);
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.ProbeError = "Permission denied";
        var jobId = await EnqueueScanAsync(library);
        using var locked = UnreadablePath.Create(path);
        if (locked is null)
        {
            return;
        }

        await RunScanAsync(jobId);

        var file = Assert.Single(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
        Assert.Equal(LibraryProblemKind.NoPermission, file.ProblemKind);
    }

    [WindowsFact("Only Windows refuses to open a file another program holds without sharing.")]
    public async Task A_file_another_program_holds_is_listed_as_unreadable()
    {
        var library = await LibraryAsync();
        await ScanFolderAsync(library);
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.ProbeError = "Invalid data found when processing input";
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await RunScanAsync(await EnqueueScanAsync(library));

        var file = Assert.Single(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
        Assert.Equal(LibraryProblemKind.Unreadable, file.ProblemKind);
    }

    [Fact]
    public async Task A_link_count_that_cannot_be_read_is_left_unknown_and_the_file_is_still_classified()
    {
        var library = await LibraryAsync();
        await ScanFolderAsync(library);
        await File.WriteAllBytesAsync(_libraryFolder.Join("film.mkv"), [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishOnly;
        var arguments = HandlerArguments();
        arguments[3] = new UnreadableLinkCount();
        var handler = (LibraryScanHandler)Activator.CreateInstance(typeof(LibraryScanHandler), arguments)!;
        var jobId = await EnqueueScanAsync(library);
        var claimed = await _fixture.Jobs.ClaimNextAsync("test-worker", _fixture.Store.Clock.GetUtcNow().AddMinutes(5), _fixture.Store.Clock.GetUtcNow());

        await handler.HandleAsync(new JobWorkContext(claimed!.Id, claimed.JobKind, claimed.PayloadJson, "test-worker"), CancellationToken.None);

        var file = Assert.Single(await _fixture.Db(uow => _scans.CurrentFilesAsync(uow, library), commit: false));
        Assert.Equal(jobId, claimed.Id);
        Assert.Null(file.LinkCount);
        Assert.Equal(LibraryFileClassification.Matches, file.Classification);
    }
}
