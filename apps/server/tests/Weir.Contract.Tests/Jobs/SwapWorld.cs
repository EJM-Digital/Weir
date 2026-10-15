using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// A server with a library of one film that has a track to clean, and a clean of that film that the server is stopped part-way through.
/// The swap stops itself for good at the point a test names (<c>WEIR_LIBRARY_SWAP_HOLD_AT</c>), which says so in a file in Weir's
/// home; the test ends the process there, so the files on disk are exactly what a crash at that point leaves.
/// </summary>
internal sealed class SwapWorld : IAsyncDisposable
{
    public const string HoldSetting = "WEIR_LIBRARY_SWAP_HOLD_AT";

    /// <summary>The server only honours <see cref="HoldSetting"/> when it is not running as a production install.</summary>
    public const string NotProductionSetting = "WEIR_ENV";

    public const string NotProduction = "development";

    private const string HeldFileName = "swap-held-at.txt";
    private const string WorkersSetting = "WEIR_PROCESSING_WORKER_COUNT";
    private const string CleanedLanguage = "eng";
    private const string DroppedLanguage = "fre";

    private readonly FakeFfmpeg _tools;
    private readonly TemporaryFolder _folders;

    private SwapWorld(FakeFfmpeg tools, TemporaryFolder folders, WeirServer server, WeirClient client, long libraryId, string libraryFolder)
    {
        _tools = tools;
        _folders = folders;
        Server = server;
        Client = client;
        LibraryId = libraryId;
        LibraryFolder = libraryFolder;
        FilePath = Path.Combine(libraryFolder, "Film (2020)", "Film (2020).mkv");
    }

    /// <summary>What the film is before it is cleaned: English and French audio.</summary>
    public static byte[] OriginalBytes { get; } = FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: [CleanedLanguage, DroppedLanguage]), padding: 4096);

    public WeirServer Server { get; }

    public WeirClient Client { get; private set; }

    public long LibraryId { get; }

    public string LibraryFolder { get; }

    /// <summary>The film, under the name it has in the library.</summary>
    public string FilePath { get; }

    public string BackupPath => Path.ChangeExtension(FilePath, null) + ".weir-bak" + Path.GetExtension(FilePath);

    /// <summary>Where a kept original goes by default: the library folder's own originals folder.</summary>
    public string KeptFolder => Path.Combine(LibraryFolder, ".weir-originals");

    /// <summary>Where the original is kept by default, which is the place reserved for it before the commit.</summary>
    public string KeptPath => Path.Combine(KeptFolder, "Film (2020)", "Film (2020).mkv");

    private string HeldFile => Path.Combine(Server.Home, HeldFileName);

    /// <summary>
    /// Starts a server and scans the library so the film is ready to clean. <paramref name="holdAt"/> names the swap point to stop at;
    /// <paramref name="workers"/> is how many job workers the server runs.
    /// </summary>
    public static async Task<SwapWorld> StartAsync(bool keepOriginal, string? holdAt = null, int workers = 1)
    {
        var tools = FakeFfmpeg.Install();
        var folders = new TemporaryFolder();
        WeirServer? server = null;
        WeirClient? client = null;
        try
        {
            var libraryFolder = Directory.CreateDirectory(Path.Combine(folders.Path, "library")).FullName;
            var film = Path.Combine(libraryFolder, "Film (2020)", "Film (2020).mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(film)!);
            await File.WriteAllBytesAsync(film, OriginalBytes);

            var environment = tools.Env.With((WorkersSetting, workers.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (holdAt is not null)
            {
                environment[HoldSetting] = holdAt;
                environment[NotProductionSetting] = NotProduction;
            }

            server = await WeirServer.StartNewAsync(environment);
            client = await server.CreateAdminClientAsync();
            var library = await JobsApi.LibraryForScopeAsync(client, "movie");
            var libraryId = (long)library["id"]!;
            var saved = await client.PutWithCsrfAsync(
                $"{WeirClient.Api}/processing/libraries/{libraryId}/library-settings",
                new JsonObject
                {
                    ["library_folders"] = new JsonArray(libraryFolder),
                    ["keep_original_after_clean"] = keepOriginal,
                });
            JobsApi.Expect(saved, HttpStatusCode.OK);

            var world = new SwapWorld(tools, folders, server, client, libraryId, libraryFolder);
            await world.ScanAsync();
            return world;
        }
        catch
        {
            client?.Dispose();
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            folders.Dispose();
            tools.Dispose();
            throw;
        }
    }

    /// <summary>Asks for the film to be cleaned. The job runs on a worker; the swap's stop is awaited with <see cref="HeldAtAsync"/>.</summary>
    public async Task RequestCleanAsync()
    {
        var response = await Client.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/libraries/{LibraryId}/library-files/clean",
            new JsonObject
            {
                ["paths"] = new JsonArray(FilePath),
                ["confirm_final_removal"] = true,
            });
        JobsApi.Expect(response, HttpStatusCode.OK);
    }

    /// <summary>
    /// Waits until the swap has reached the point it was told to stop at, and returns the name of that point. The held clean keeps its
    /// lease, so this waits as long as a worker has it (see <see cref="WaitWhileWorkedOnAsync{T}"/>).
    /// </summary>
    public async Task<string> HeldAtAsync() =>
        await WaitWhileWorkedOnAsync(
            () => Task.FromResult(File.Exists(HeldFile) ? File.ReadAllText(HeldFile) : null),
            "the swap to stop at the point it was told to");

    /// <summary>Waits for <paramref name="result"/> for as long as a worker holds any of the server's jobs (see <see cref="JobWait"/>).</summary>
    public async Task<T> WaitWhileWorkedOnAsync<T>(Func<Task<T?>> result, string what)
        where T : class =>
        await new JobWait().UntilAsync(async () => [.. (await JobsApi.AllJobsAsync(Client)).Select(job => (string)job["status"]!)], result, what);

    public async Task WaitWhileWorkedOnAsync(Func<Task<bool>> condition, string what) =>
        await WaitWhileWorkedOnAsync(async () => await condition() ? "done" : null, what);

    /// <summary>Ends the server's process as a crash or a power cut would, with the swap where it stopped.</summary>
    public Task CrashAsync() => Server.StopAsync();

    /// <summary>Starts the server again on the same files and data, with the swap no longer held and <paramref name="workers"/> workers.</summary>
    public async Task RestartAsync(int workers)
    {
        Client.Dispose();
        File.Delete(HeldFile);
        await Server.RestartAsync(new Dictionary<string, string>
        {
            [HoldSetting] = string.Empty,
            [WorkersSetting] = workers.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        Client = await Server.CreateAdminClientAsync();
    }

    /// <summary>Every file under the library folder, as a path relative to it, with the kept originals folder included.</summary>
    public string[] FilesInLibrary() =>
        [.. Directory.EnumerateFiles(LibraryFolder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(LibraryFolder, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>The state each swap was left in on the database, once the server has been stopped to read it.</summary>
    public async Task<string[]> SwapStatesAsync()
    {
        await using var database = await Server.StopForDatabaseAsync(restart: false);
        return [.. SeedSql.Rows(database.Connection, "SELECT state FROM library_swaps ORDER BY job_id").Select(row => (string)row["state"]!)];
    }

    /// <summary>Removes the database's record of every swap, as if it had never been written, so only the files show one was started.</summary>
    public async Task ForgetTheSwapsAsync()
    {
        await using var database = await Server.StopForDatabaseAsync(restart: false);
        SeedSql.Execute(database.Connection, "DELETE FROM library_swaps");
    }

    /// <summary>The Activity entries that say a clean of the film failed.</summary>
    public Task<JsonObject[]> FailedCleansAsync() => ActivityOfTypeAsync("library.file_failed");

    /// <summary>The recent Activity entries of one type.</summary>
    public async Task<JsonObject[]> ActivityOfTypeAsync(string eventType)
    {
        var recent = await Client.GetAsync($"{WeirClient.Api}/activity/recent", ("limit", 100));
        JobsApi.Expect(recent, HttpStatusCode.OK);
        return [.. recent.Fields["items"]!.AsArray().Select(item => item!.AsObject()).Where(item => (string?)item["event_type"] == eventType)];
    }

    /// <summary>The status of the clean job, as the jobs list shows it.</summary>
    public async Task<JsonObject> CleanJobAsync() =>
        (await JobsApi.AllJobsAsync(Client)).Single(job => (string?)job["job_kind"] == "processing.library.clean.v1");

    /// <summary>The kept originals, which are each the film as it was before the clean.</summary>
    public async Task<bool> KeptOriginalsAreUncleanedAsync() =>
        Directory.Exists(KeptFolder)
        && await AllAsync(Directory.EnumerateFiles(KeptFolder, "*", SearchOption.AllDirectories), async path => (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(OriginalBytes));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
        _folders.Dispose();
        _tools.Dispose();
    }

    private async Task ScanAsync()
    {
        var scan = await Client.PostWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{LibraryId}/library-scan", new JsonObject());
        JobsApi.Expect(scan, HttpStatusCode.OK);
        await WaitWhileWorkedOnAsync(
            async () =>
            {
                var files = await Client.GetAsync($"{WeirClient.Api}/processing/libraries/{LibraryId}/library-files");
                JobsApi.Expect(files, HttpStatusCode.OK);
                return files.Fields["files"]!.AsArray().Any(file => (string?)file!["status"] == "needs_cleaning");
            },
            "the scan to find the film that needs cleaning");
    }

    private static async Task<bool> AllAsync(IEnumerable<string> paths, Func<string, Task<bool>> test)
    {
        foreach (var path in paths)
        {
            if (!await test(path))
            {
                return false;
            }
        }

        return true;
    }
}
