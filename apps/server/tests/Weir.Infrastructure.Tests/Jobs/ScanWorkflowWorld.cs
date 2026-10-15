using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// A real database and a real watched folder for tests of what one workflow's saved settings do to a scan: workflows are created
/// from the settings a test names, files are written into the folder, and the scan and its queued passes are read back.
/// </summary>
internal sealed class ScanWorkflowWorld : IDisposable
{
    private static readonly LibraryStore Libraries = new();
    private static readonly FileStateStore Files = new();

    private readonly ProcessingJobStore _jobs;
    private readonly ProcessingWatchedFolderScanDispatchJobHandler _handler;

    private ScanWorkflowWorld(StoreFixture store, ProcessingJobStore jobs, ProcessingWatchedFolderScanDispatchJobHandler handler)
    {
        Store = store;
        _jobs = jobs;
        _handler = handler;
        Watched = store.Home.Join("watch");
        Output = store.Home.Join("out");
        Directory.CreateDirectory(Watched);
        Directory.CreateDirectory(Output);
    }

    public StoreFixture Store { get; }

    public string Watched { get; }

    public string Output { get; }

    public static async Task<ScanWorkflowWorld> StartAsync()
    {
        var (store, jobs, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        return new ScanWorkflowWorld(store, jobs, handler);
    }

    public void Dispose() => Store.Dispose();

    /// <summary>
    /// A movie workflow over the world's folders. It waits for nothing and takes any size unless <paramref name="settings"/> says
    /// otherwise, so a test names only the setting it is about.
    /// </summary>
    public async Task<long> WorkflowAsync(Func<ProcessingLibraryInput, ProcessingLibraryInput>? settings = null, string? name = null)
    {
        var input = new ProcessingLibraryInput
        {
            Name = name ?? "Movies " + Guid.NewGuid().ToString("N")[..8],
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = Watched,
            OutputFolder = Output,
            ReadyAfterSeconds = 0,
            MinFileSizeMb = 0,
        };
        await using var uow = await UnitOfWork.OpenAsync(Store.Database);
        var created = await Libraries.CreateAsync(uow, settings is null ? input : settings(input));
        await uow.CommitAsync();
        return created.Id;
    }

    /// <summary>Writes a file of <paramref name="bytes"/> bytes under the watched folder, making the folders on the way.</summary>
    public string Write(string relativePath, int bytes = 1)
    {
        var path = Path.Combine(Watched, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>One scan of the workflow that queues the passes it finds.</summary>
    public Task ScanAsync(long libraryId) =>
        ProcessingWatchedFolderScanDispatchJobHandlerTests.RunScanAsync(_handler, _jobs, libraryId, enqueueRemuxJobs: true);

    /// <summary>The relative path of every pass the scans have queued, in path order.</summary>
    public async Task<IReadOnlyList<string>> QueuedPathsAsync()
    {
        var payloads = new List<string>();
        using var connection = Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            payloads.Add(reader.GetString(0));
        }

        return [.. payloads.Select(json => ((WireString)((WireObject)WireJsonParser.Parse(json)).Get("relative_media_path")!).Value).Order(StringComparer.Ordinal)];
    }

    /// <summary>What the scan recorded for a file, or null when it never listed it.</summary>
    public async Task<ProcessingFileRecord?> RowAsync(long libraryId, string relativePath)
    {
        await using var uow = await UnitOfWork.OpenAsync(Store.Database);
        return await Files.FindAsync(uow, libraryId, relativePath);
    }
}
