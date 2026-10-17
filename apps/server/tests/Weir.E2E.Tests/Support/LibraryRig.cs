using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A Weir of its own with one movie workflow whose library folder holds films, for a test that works the Library page: it scans,
/// cleans and asks for files again as a person does. The server runs one worker against the fake ffprobe and ffmpeg, which read
/// each film's tracks from the film itself, and a Radarr that lists the films (and, for "Download again", deletes one and
/// searches) can be connected. Everything the rig changes goes through Weir's API; what a test reads back comes from the same API
/// or from the disk, never from the database.
/// </summary>
public sealed class LibraryRig : IAsyncDisposable
{
    private const string Api = WeirClient.Api;
    private const string CleanJobKind = "processing.library.clean.v1";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(180);

    private readonly FakeFfmpeg _tools;
    private readonly WeirServer _server;
    private readonly WeirClient _admin;
    private readonly TemporaryFolder _folders;
    private readonly FakeManager? _radarr;

    private LibraryRig(FakeFfmpeg tools, WeirServer server, WeirClient admin, TemporaryFolder folders, FakeManager? radarr, long libraryId, string libraryFolder)
    {
        _tools = tools;
        _server = server;
        _admin = admin;
        _folders = folders;
        _radarr = radarr;
        LibraryId = libraryId;
        LibraryFolder = libraryFolder;
    }

    /// <summary>The server's address without a trailing slash.</summary>
    public string BaseUrl => _server.BaseUrl.GetLeftPart(UriPartial.Authority);

    public long LibraryId { get; }

    /// <summary>The folder the workflow's library scan reads, which every film lives in.</summary>
    public string LibraryFolder { get; }

    /// <summary>The Radarr connected to the server; throws when the rig was started without one.</summary>
    public FakeManager Radarr => _radarr ?? throw new InvalidOperationException("The rig was started without a Radarr.");

    /// <param name="withRadarr">Connects a Radarr, which lists every film the test adds with <see cref="AddFilmAsync"/>.</param>
    public static async Task<LibraryRig> StartAsync(bool withRadarr = false)
    {
        var tools = FakeFfmpeg.Install();
        var folders = new TemporaryFolder();
        var radarr = withRadarr ? FakeManager.StartArr("radarr") : null;
        WeirServer? server = null;
        WeirClient? admin = null;
        try
        {
            server = await WeirServer.StartNewAsync(new Dictionary<string, string>(tools.Env)
            {
                ["WEIR_WEB_DIST"] = RepoPaths.WebDist,
                ["WEIR_PROCESSING_WORKER_COUNT"] = "1",
            });
            admin = new WeirClient(server.BaseUrl);
            await admin.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            var libraryFolder = Directory.CreateDirectory(Path.Combine(folders.Path, "Movies")).FullName;
            var libraryId = await CreateWorkflowAsync(admin, folders.Path, libraryFolder);
            if (radarr is not null)
            {
                var connected = await admin.PostWithCsrfAsync($"{Api}/media-managers/connections", new JsonObject
                {
                    ["kind"] = "radarr",
                    ["base_url"] = radarr.BaseUrl,
                    ["api_key"] = radarr.ApiKey,
                    ["enabled"] = true,
                });
                Assert.True(connected.Status == HttpStatusCode.Created, connected.ToString());
            }

            return new LibraryRig(tools, server, admin, folders, radarr, libraryId, libraryFolder);
        }
        catch
        {
            admin?.Dispose();
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            radarr?.Dispose();
            tools.Dispose();
            folders.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A film in the library folder with these tracks (the fake ffprobe reads them from the film). When the rig has a Radarr, the
    /// Radarr lists the film as movie <paramref name="radarrMovieId"/> with a file at that path.
    /// </summary>
    public async Task<string> AddFilmAsync(string relativePath, string[] audioLanguages, string[] subtitleLanguages, long radarrMovieId = 0, string? radarrTitle = null)
    {
        var path = Path.Combine(LibraryFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: audioLanguages, subtitleLanguages: subtitleLanguages)));
        if (_radarr is not null && radarrMovieId != 0)
        {
            _radarr.Library.Add(new JsonObject
            {
                ["id"] = radarrMovieId,
                ["title"] = radarrTitle,
                ["hasFile"] = true,
                ["movieFile"] = new JsonObject { ["id"] = radarrMovieId * 10, ["size"] = new FileInfo(path).Length, ["path"] = path },
            });
        }

        return path;
    }

    /// <summary>
    /// A film that already is what the rules ask for when they keep only <paramref name="audioLanguage"/> and remove every subtitle: the video and the
    /// audio are flagged as the defaults, so the scan reads it as matching and there is nothing to reorder or re-tag.
    /// </summary>
    public async Task<string> AddMatchingFilmAsync(string relativePath, string audioLanguage)
    {
        var path = Path.Combine(LibraryFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var probe = FakeMedia.Probe(audioLanguages: [audioLanguage]);
        foreach (var stream in probe["streams"]!.AsArray())
        {
            stream!["disposition"] = new JsonObject { ["default"] = 1, ["forced"] = 0 };
        }

        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(probe));
        return path;
    }

    /// <summary>
    /// The Radarr deletes the file of a movie it is asked about and takes the search that follows, as it does for "Download again".
    /// The movie it answers with is the one it lists, which is the shape Radarr's OpenAPI describes, not a recorded reply.
    /// </summary>
    public void RadarrDeletesFiles()
    {
        Radarr.Route("GET", "/api/v3/movie/{id}", request =>
            Reply.Ok(Radarr.Library.Snapshot().Single(movie => movie["id"]!.ToString() == LastSegment(request.Path)).DeepClone()));
        Radarr.Route("DELETE", "/api/v3/moviefile/{id}", new Reply(200));
    }

    /// <summary>How many requests Radarr has received so far, for <see cref="RadarrAskedAboutAFileSince"/>.</summary>
    public int RadarrMark() => Radarr.Requests.Count;

    /// <summary>
    /// What Weir asked Radarr to do about one file since <paramref name="mark"/>: look a movie up, delete its file, send a command.
    /// Weir's own checks of the connection and of the library listing are not among them.
    /// </summary>
    public IReadOnlyList<string> RadarrAskedAboutAFileSince(int mark) =>
        [.. Radarr.Requests.Skip(mark)
            .Where(request => request.Path.StartsWith("/api/v3/movie/", StringComparison.Ordinal)
                || request.Path.StartsWith("/api/v3/moviefile", StringComparison.Ordinal)
                || request.Path == "/api/v3/command")
            .Select(request => $"{request.Method} {request.Path}")];

    /// <summary>
    /// The clean of this film stays under way, once a worker takes it, until <see cref="ReleaseClean"/>: the Files table shows it as
    /// cleaning for as long as a test wants to look.
    /// </summary>
    public void HoldClean(string film) =>
        _tools.SetFileRule(Path.GetFileName(film), new FileRule { RemuxReleaseFile = ReleaseFileFor(film) });

    /// <summary>Lets the clean of this film, held by <see cref="HoldClean"/>, finish.</summary>
    public void ReleaseClean(string film) => File.WriteAllText(ReleaseFileFor(film), string.Empty);

    /// <summary>Scans the library and waits until that scan has finished.</summary>
    public async Task ScanAsync()
    {
        var started = await _admin.PostWithCsrfAsync($"{Api}/processing/libraries/{LibraryId}/library-scan");
        Assert.True(started.Status == HttpStatusCode.OK, started.ToString());
        var jobId = (long)started.Fields["job_id"]!;
        await Poll.UntilAsync(
            async () =>
            {
                var scan = (await FilesAsync()).Fields["scan"]!;
                return (long)scan["job_id"]! == jobId && (string)scan["status"]! == "completed" ? scan : null;
            },
            "the library scan to finish",
            Ceiling);
    }

    /// <summary>A new rules profile and the library cleaning by it; the profile keeps these audio languages and removes every subtitle.</summary>
    public async Task UseRulesKeepingAudioAsync(string profileName, params string[] audioLanguages)
    {
        var made = await _admin.PostWithCsrfAsync($"{Api}/processing/rule-sets", RuleSet(profileName, audioLanguages));
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var saved = await _admin.PutWithCsrfAsync(
            $"{Api}/processing/libraries/{LibraryId}/library-settings",
            new JsonObject { ["library_folders"] = new JsonArray(LibraryFolder), ["library_rule_set_id"] = (long)made.Fields["id"]! });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    /// <summary>
    /// Queues the clean the web sends once the person has confirmed, for the paths as the Files table lists them, and waits for what it
    /// did to each file. A connected Radarr is told the file changed, so this waits for that too: nothing the clean does is left to
    /// arrive during a test that follows.
    /// </summary>
    public async Task CleanAsync(params string[] paths)
    {
        var listed = new List<string>();
        foreach (var path in paths)
        {
            listed.Add((string)(await FileRowAsync(path))["path"]!);
        }

        var queued = await _admin.PostWithCsrfAsync($"{Api}/processing/libraries/{LibraryId}/library-files/clean", new JsonObject
        {
            ["paths"] = new JsonArray([.. listed.Select(path => (JsonNode?)JsonValue.Create(path))]),
            ["confirm_final_removal"] = true,
        });
        Assert.True(queued.Status == HttpStatusCode.OK, queued.ToString());
        await Poll.UntilAsync(async () => (await CleansAsync()).Count >= paths.Length, "the clean to finish", Ceiling);
        if (_radarr is not null)
        {
            await _radarr.WaitForRequestAsync("POST", "/api/v3/command", timeout: Ceiling);
        }
    }

    /// <summary>The library files as the Files table lists them, in the order the server gives when it is asked for <paramref name="query"/>.</summary>
    public async Task<WeirResponse> FilesAsync(params (string Name, object Value)[] query)
    {
        var response = await _admin.GetAsync($"{Api}/processing/libraries/{LibraryId}/library-files", query);
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response;
    }

    /// <summary>The Files table row of the film at <paramref name="path"/>.</summary>
    public async Task<JsonObject> FileRowAsync(string path) =>
        (await FilesAsync()).Fields["files"]!.AsArray().Select(file => file!.AsObject()).Single(file => SamePath((string)file["path"]!, path));

    /// <summary>What Activity lists as cleans of this library's files: one entry for each file a clean has finished with.</summary>
    public async Task<IReadOnlyList<JsonObject>> CleansAsync()
    {
        var response = await _admin.GetAsync($"{Api}/processing/library-cleans", ("library_id", LibraryId));
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return [.. response.Fields["cleans"]!.AsArray().Select(clean => clean!.AsObject())];
    }

    /// <summary>The clean jobs the queue holds, whatever their status.</summary>
    public async Task<int> CleanJobCountAsync()
    {
        var response = await _admin.GetAsync($"{Api}/processing/jobs/inspection", ("limit", 100));
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response.Fields["jobs"]!.AsArray().Count(job => (string)job!["job_kind"]! == CleanJobKind);
    }

    /// <summary>The library's settings as the server holds them, such as whether the daily clean is on.</summary>
    public async Task<JsonObject> SettingsAsync()
    {
        var response = await _admin.GetAsync($"{Api}/processing/libraries/{LibraryId}/library-settings");
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response.Fields;
    }

    /// <summary>The titles the server says are missing tracks the library's rules would now keep.</summary>
    public async Task<JsonObject> RedownloadsAsync()
    {
        var response = await _admin.GetAsync($"{Api}/processing/libraries/{LibraryId}/library-redownloads");
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response.Fields;
    }

    /// <summary>Whether Activity's log holds an entry of this type.</summary>
    public async Task<bool> LoggedAsync(string eventType)
    {
        var response = await _admin.GetAsync($"{Api}/activity/recent", ("limit", 100));
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return response.Text.Contains(eventType, StringComparison.Ordinal);
    }

    /// <summary>A hash of the film's bytes, to tell that nothing rewrote it.</summary>
    public static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _server.DisposeAsync();
        _radarr?.Dispose();
        _tools.Dispose();
        _folders.Dispose();
    }

    private string ReleaseFileFor(string film) => Path.Combine(_folders.Path, $"release-{Path.GetFileName(film)}");

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static JsonObject RuleSet(string name, string[] audioLanguages) => new()
    {
        ["name"] = name,
        ["primary_audio_lang"] = audioLanguages[0],
        ["secondary_audio_lang"] = audioLanguages.Length > 1 ? audioLanguages[1] : string.Empty,
        ["subtitle_mode"] = "remove_all",
    };

    private static async Task<long> CreateWorkflowAsync(WeirClient admin, string root, string libraryFolder)
    {
        var created = await admin.PostWithCsrfAsync($"{Api}/processing/libraries", new JsonObject
        {
            ["name"] = "E2E library films",
            ["media_type"] = "movie",
            ["watched_folder"] = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName,
            ["output_folder"] = Directory.CreateDirectory(Path.Combine(root, "Cleaned")).FullName,
            ["minimum_free_disk_space_mb"] = 0,
        });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var libraryId = (long)created.Fields["id"]!;
        var saved = await admin.PutWithCsrfAsync(
            $"{Api}/processing/libraries/{libraryId}/library-settings",
            new JsonObject { ["library_folders"] = new JsonArray(libraryFolder) });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        return libraryId;
    }
}
