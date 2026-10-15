using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// One server that works library files with the real ffprobe, ffmpeg and (when installed) mkvmerge, and one workflow whose library
/// folder holds real media generated for the test. Every step goes through the public API; the only things the test controls from
/// outside are the ones Weir does not own, the files on disk.
/// </summary>
internal sealed class LibraryCleanWorld : IAsyncDisposable
{
    public const string LibrariesUrl = Api + "/processing/libraries";

    private readonly TemporaryFolder _root;

    private LibraryCleanWorld(TemporaryFolder root, WeirServer server, WeirClient admin, long libraryId, string folder)
    {
        _root = root;
        Server = server;
        Admin = admin;
        LibraryId = libraryId;
        Folder = folder;
    }

    public WeirServer Server { get; }

    /// <summary>Signed in as the admin; replaced by a new session after the server restarts.</summary>
    public WeirClient Admin { get; private set; }

    public long LibraryId { get; }

    /// <summary>The library folder every test file lives in.</summary>
    public string Folder { get; }

    /// <summary>Where Weir keeps an original unless the workflow names another folder: inside the library folder.</summary>
    public string OriginalsFolder => Path.Combine(Folder, ".weir-originals");

    /// <summary>A folder beside the library, for a workflow that names its own originals folder.</summary>
    public string CustomOriginalsFolder => Path.Combine(_root.Path, "Kept originals");

    /// <summary>Where a download client keeps the files it is still seeding.</summary>
    public string DownloadsFolder => Path.Combine(_root.Path, "Downloads");

    private string FilesUrl => $"{LibrariesUrl}/{LibraryId}/library-files";

    /// <summary>
    /// A server and a movie workflow whose library folder is empty. <paramref name="keepOriginal"/> turns on "keep the original after
    /// clean". <paramref name="withViewer"/> seeds the read-only account <c>bob</c> beside the admin.
    /// </summary>
    public static async Task<LibraryCleanWorld> StartAsync(
        bool keepOriginal = false, bool withViewer = false, bool customOriginalsFolder = false, params (string Name, JsonNode Value)[] workflow)
    {
        var server = await WeirServer.StartNewAsync(RealFfmpeg.Env.With(("WEIR_PROCESSING_WORKER_COUNT", "1")));
        var root = new TemporaryFolder();
        WeirClient? admin = null;
        try
        {
            if (withViewer)
            {
                await using var database = await server.StopForDatabaseAsync();
                SeededAccounts.EnsureAdmin(database.Connection);
                SeededAccounts.EnsureViewer(database.Connection);
            }

            admin = await server.CreateAdminClientAsync();
            var folder = Path.Combine(root.Path, "Movies");
            Directory.CreateDirectory(folder);
            var libraryId = await CreateWorkflowAsync(admin, root.Path, folder, keepOriginal, customOriginalsFolder, workflow);
            return new LibraryCleanWorld(root, server, admin, libraryId, folder);
        }
        catch
        {
            admin?.Dispose();
            await server.DisposeAsync();
            root.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Server.DisposeAsync();
        _root.Dispose();
    }

    /// <summary>Generates a film in the library folder, in the container its extension names, with its first audio track flagged default.</summary>
    public async Task<string> AddFilmAsync(string relativePath, string[] audioLanguages, string[] subtitleLanguages)
    {
        var path = Path.Combine(Folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Processing.RealMedia.GenerateAsync(path, seconds: 3, audioLanguages, subtitleLanguages, firstAudioDefault: true);
        return path;
    }

    /// <summary>Signs in again after a restart, which comes back on a new port.</summary>
    public async Task RestartServerAsync()
    {
        await Server.RestartAsync();
        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }

    /// <summary>Scans the library and waits until that scan has finished.</summary>
    public async Task ScanAsync()
    {
        var started = await Admin.PostWithCsrfAsync($"{LibrariesUrl}/{LibraryId}/library-scan");
        started.ShouldBe(HttpStatusCode.OK);
        var jobId = (long)started.Fields["job_id"]!;
        await new JobWait().UntilAsync(
            async () => [(string)(await JobAsync(jobId))["status"]!],
            async () =>
            {
                var scan = (await FilesBodyAsync())["scan"]!;
                Assert.Equal(jobId, (long)scan["job_id"]!);
                return (string)scan["status"]! == "completed" ? scan : null;
            },
            "the library scan");
    }

    /// <summary>The library's files as the Files table lists them.</summary>
    public async Task<JsonObject> FilesBodyAsync(string? query = null)
    {
        var response = await Admin.GetAsync(FilesUrl + (query is null ? string.Empty : "?" + query));
        response.ShouldBe(HttpStatusCode.OK);
        return response.Fields;
    }

    /// <summary>The Files table row of the file at <paramref name="path"/>.</summary>
    public async Task<JsonObject> FileAsync(string path) =>
        (await FilesBodyAsync())["files"]!.AsArray().Select(file => file!.AsObject()).Single(file => SamePath((string)file["path"]!, path));

    /// <summary>
    /// What the web sends to clean files (<c>cleanLibraryFiles</c> in <c>library-mode-api.ts</c>): the chosen paths and whether the
    /// person confirmed removing tracks, with the session's CSRF token in the body.
    /// </summary>
    public Task<WeirResponse> CleanAsync(IEnumerable<string> paths, bool confirmFinalRemoval, WeirClient? client = null) =>
        (client ?? Admin).PostWithCsrfAsync(FilesUrl + "/clean", CleanBody(paths, confirmFinalRemoval));

    public static JsonObject CleanBody(IEnumerable<string> paths, bool confirmFinalRemoval) => new()
    {
        ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode?)JsonValue.Create(path))]),
        ["confirm_final_removal"] = confirmFinalRemoval,
    };

    /// <summary>Queues the clean the web would after the person confirms, for the file as the Files table lists it, and returns the job it queued.</summary>
    public async Task<long> ConfirmedCleanAsync(string path)
    {
        var response = await CleanAsync([(string)(await FileAsync(path))["path"]!], confirmFinalRemoval: true);
        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(1, (int)response.Fields["queued"]!);
        return (long)response.Fields["job_ids"]![0]!;
    }

    /// <summary>
    /// Waits for what the newest clean did to the file at <paramref name="path"/> (Activity's library cleans) and returns that entry, for as
    /// long as a worker has the clean (see <see cref="JobWait"/>).
    /// </summary>
    public async Task<JsonObject> WaitForCleanAsync(string path) =>
        await new JobWait().UntilAsync(
            async () => [.. (await CleanJobsAsync()).Select(job => (string)job["status"]!)],
            async () =>
            {
                var response = await Admin.GetAsync($"{Api}/processing/library-cleans", ("library_id", LibraryId));
                response.ShouldBe(HttpStatusCode.OK);
                return response.Fields["cleans"]!.AsArray().Select(clean => clean!.AsObject())
                    .FirstOrDefault(clean => SamePath((string)clean["relative_path"]!, path));
            },
            $"the clean of {Path.GetFileName(path)}");

    /// <summary>The job as the inspection list shows it, whatever its status.</summary>
    public async Task<JsonObject> JobAsync(long jobId)
    {
        var response = await Admin.GetAsync($"{Api}/processing/jobs/inspection", ("limit", 100));
        response.ShouldBe(HttpStatusCode.OK);
        return response.Fields["jobs"]!.AsArray().Select(job => job!.AsObject()).Single(job => (long)job["id"]! == jobId);
    }

    /// <summary>Lets the workflow clean a file that is also linked under another name (the library setting "clean hardlinked files").</summary>
    public async Task AllowHardlinkedFilesAsync()
    {
        var saved = await Admin.PutWithCsrfAsync(
            $"{LibrariesUrl}/{LibraryId}/library-settings",
            Obj(("library_folders", Strings([Folder])), ("clean_hardlinked_files", true)));
        saved.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Pauses or resumes processing, the header's Pause button.</summary>
    public async Task PauseAsync(bool paused)
    {
        var response = await Admin.PutWithCsrfAsync($"{Api}/pause", Obj(("paused", paused), ("scan_while_paused", false)));
        response.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The clean jobs the queue holds, whatever their status.</summary>
    public async Task<IReadOnlyList<JsonObject>> CleanJobsAsync()
    {
        var response = await Admin.GetAsync($"{Api}/processing/jobs/inspection", ("limit", 100));
        response.ShouldBe(HttpStatusCode.OK);
        return [.. response.Fields["jobs"]!.AsArray().Select(job => job!.AsObject()).Where(job => (string)job["job_kind"]! == "processing.library.clean.v1")];
    }

    /// <summary>Makes a postponed job due now, the way the wait it was given running out would, and signs in again on the restarted server.</summary>
    public async Task MakeJobDueAsync(long jobId)
    {
        await using (var database = await Server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, "UPDATE jobs SET not_before = NULL WHERE id = $id", ("$id", jobId));
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }

    /// <summary>Gives <paramref name="existing"/> a second name, as a download client's seeding copy of a library file is.</summary>
    public static async Task HardLinkAsync(string existing, string link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/H", link, existing } }
            : new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { existing, link } };
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"could not link {link} to {existing}: {await error}");
    }

    /// <summary>Every file in the library folder, the originals folder included, as paths below the library folder.</summary>
    public IReadOnlyList<string> FilesOnDisk() =>
        [.. Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Folder, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>Fails when Weir's temporary copy or backup of any file is still in the library.</summary>
    public void AssertNoSwapLeftovers() =>
        Assert.DoesNotContain(FilesOnDisk(), file => file.Contains(".weir-tmp", StringComparison.Ordinal) || file.Contains(".weir-bak", StringComparison.Ordinal));

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<long> CreateWorkflowAsync(
        WeirClient admin, string root, string libraryFolder, bool keepOriginal, bool customOriginalsFolder, (string Name, JsonNode Value)[] overrides)
    {
        var body = new JsonObject
        {
            ["name"] = "Contract Films",
            ["media_type"] = "movie",
            ["watched_folder"] = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName,
            ["output_folder"] = Directory.CreateDirectory(Path.Combine(root, "Cleaned")).FullName,
            // A workflow keeps 5 GB free by default; a few-kilobyte film is cleaned whatever the drive holds.
            ["minimum_free_disk_space_mb"] = 0,
        };
        foreach (var (name, value) in overrides)
        {
            body[name] = value.DeepClone();
        }

        var created = await admin.PostWithCsrfAsync(LibrariesUrl, body);
        created.ShouldBe(HttpStatusCode.Created);
        var libraryId = (long)created.Fields["id"]!;
        var saved = await admin.PutWithCsrfAsync(
            $"{LibrariesUrl}/{libraryId}/library-settings",
            Obj(
                ("library_folders", Strings([libraryFolder])),
                ("keep_original_after_clean", keepOriginal),
                ("originals_folder", customOriginalsFolder ? Path.Combine(root, "Kept originals") : string.Empty)));
        saved.ShouldBe(HttpStatusCode.OK);
        return libraryId;
    }
}
