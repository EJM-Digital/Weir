using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.MediaManagers;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// A Deluno-linked workflow's manager check over real HTTP against a scripted Deluno that publishes (or does not publish)
/// <c>GET /api/integrations/processors/download-destinations</c>: where each download client really saves, Deluno's
/// processor path mappings, and what each answer of that route (200, 404, 403, no answer) makes the check say.
/// </summary>
public sealed class DelunoDownloadDestinationsApiTests
{
    private const string SetupPath = "/api/v1/processing/manager-setup";
    private const string DestinationsPath = "/api/integrations/processors/download-destinations";
    private const string ManifestPath = "/api/integrations/external/manifest";
    private const string Watched = "/media/downloads/complete/tv";
    private const string Output = "/media/downloads/weir/tv";

    private sealed record ClientDestination(
        string Client,
        string Category,
        string? SaveFolder,
        string Status = "ok",
        string Message = "",
        string SavedBy = "deluno-per-grab");

    private sealed record ManifestLibrary(string Id, string Name, string DownloadsPath, string OutputPath);

    private static string ManifestJson(params ManifestLibrary[] libraries) => JsonSerializer.Serialize(
        new
        {
            libraries = libraries.Select(library => new Dictionary<string, object?>
            {
                ["id"] = library.Id,
                ["name"] = library.Name,
                ["mediaType"] = "tv",
                ["rootPath"] = "/media/tv",
                ["importWorkflow"] = "refine-before-import",
                ["processorOutputPath"] = library.OutputPath,
                ["downloadsPath"] = library.DownloadsPath,
            }),
            downloadClients = new[] { new { name = "qBittorrent", isEnabled = true, tvCategory = "tv-deluno", moviesCategory = "movies-deluno" } },
        });

    private static string DestinationsJson(
        string libraryId,
        string downloadsPath,
        string outputPath,
        IReadOnlyList<ClientDestination> destinations,
        params (string Deluno, string Weir)[] mappings) => JsonSerializer.Serialize(
        new
        {
            libraries = new[]
            {
                new
                {
                    libraryId,
                    libraryName = "TV",
                    mediaType = "tv",
                    downloadsPath,
                    processorOutputPath = outputPath,
                    importWorkflow = "refine-before-import",
                    destinations = destinations.Select(destination => new
                    {
                        downloadClientId = "client-1",
                        downloadClientName = destination.Client,
                        protocol = "qbittorrent",
                        category = destination.Category,
                        categoryKind = "category",
                        saveFolder = destination.SaveFolder,
                        clientSaveFolder = destination.SaveFolder,
                        savedBy = destination.SavedBy,
                        matchesDownloadsPath = (bool?)null,
                        status = destination.Status,
                        message = destination.Message,
                    }),
                    processorConnection = new
                    {
                        id = "weir-1",
                        name = "Weir on RIG",
                        pathMappings = mappings.Select(mapping => new { delunoPath = mapping.Deluno, processorPath = mapping.Weir }),
                    },
                },
            },
        });

    private static async Task<(WeirTestServer Server, ApiTestClient Client, ScriptedManager Manager)> StartAsync()
    {
        var manager = new ScriptedManager();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client, manager);
    }

    private static async Task<long> ConnectAsync(ApiTestClient client)
    {
        using var response = await client.PostAsync("/api/v1/media-managers/connections", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = "deluno",
            ["base_url"] = "http://192.0.2.10:5099",
            ["api_key"] = "deluno-key",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!["id"]!.GetValue<long>();
    }

    private static async Task<JsonNode> DelunoCheckAsync(ApiTestClient client)
    {
        using var response = await client.GetAsync(
            $"{SetupPath}?media_type=tv&watched_folder={Uri.EscapeDataString(Watched)}&output_folder={Uri.EscapeDataString(Output)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await Json(response))!["managers"]!.AsArray())!;
    }

    private static List<(string State, string Text)> Lines(JsonNode manager) =>
        [.. manager["lines"]!.AsArray().Select(line => (line!["state"]!.GetValue<string>(), line["text"]!.GetValue<string>()))];

    private static string LabelOf(JsonNode manager) => manager["label"]!.GetValue<string>();

    private static ScriptedManager DelunoManifest(ScriptedManager manager) =>
        manager.Json(HttpMethod.Get, ManifestPath, ManifestJson(new ManifestLibrary("lib-tv", "TV", Watched, Output)));

    private static ScriptedManager DelunoWith(ScriptedManager manager, string destinationsJson) =>
        DelunoManifest(manager).Json(HttpMethod.Get, DestinationsPath, destinationsJson);

    private static List<HttpRequestMessage> DestinationRequests(ScriptedManager manager)
    {
        lock (manager.Requests)
        {
            return [.. manager.Requests.Where(request => request.RequestUri!.AbsolutePath == DestinationsPath)];
        }
    }

    [Fact]
    public async Task A_client_saving_inside_the_watched_folder_is_verified_by_deluno_and_asked_for_once_with_the_library_and_key()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson("lib-tv", Watched, Output, [new("qBittorrent", "tv-deluno", Watched)]));

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        Assert.Equal(
            [
                ("ok", $"{label}'s TV library finishes downloads in {Watched}, inside this workflow's watched folder."),
                ("ok", $"{label}'s qBittorrent saves the \"tv-deluno\" category in {Watched}, inside this workflow's watched folder."),
                ("ok", $"{label} picks up cleaned files from {Output}, the folder this workflow writes to."),
            ],
            Lines(deluno));
        Assert.True(deluno["ready"]!.GetValue<bool>());
        var request = Assert.Single(DestinationRequests(manager));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("?libraryId=lib-tv", request.RequestUri!.Query);
        Assert.Equal("deluno-key", request.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task A_client_saving_outside_the_watched_folder_needs_a_fix_that_names_what_to_change()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson("lib-tv", Watched, Output, [new("qBittorrent", "tv-deluno", "/downloads/other", SavedBy: "client-category")]));

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        Assert.False(deluno["ready"]!.GetValue<bool>());
        Assert.Contains(
            (
                "problem",
                $"{label}'s qBittorrent saves the \"tv-deluno\" category in /downloads/other, which isn't inside this workflow's watched folder {Watched}. " +
                $"Set where qBittorrent saves the \"tv-deluno\" category to this workflow's watched folder, or set the watched folder to /downloads/other."),
            Lines(deluno));
    }

    [Fact]
    public async Task A_problem_deluno_reports_for_a_client_is_shown_in_its_words()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson(
            "lib-tv", Watched, Output, [new("qBittorrent", "tv-deluno", null, Status: "problem", Message: "qBittorrent is remote and no path mapping covers the downloads folder.")]));

        var deluno = await DelunoCheckAsync(client);

        Assert.Contains(
            ("problem", $"{LabelOf(deluno)} reports a problem with qBittorrent for the \"tv-deluno\" category: qBittorrent is remote and no path mapping covers the downloads folder."),
            Lines(deluno));
        Assert.False(deluno["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_client_deluno_could_not_get_an_answer_from_is_not_verified_rather_than_a_problem()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson("lib-tv", Watched, Output, [new("qBittorrent", "tv-deluno", null, Status: "unknown", Message: "Timed out.")]));

        var deluno = await DelunoCheckAsync(client);

        Assert.Contains(
            (
                "unverified",
                $"{LabelOf(deluno)} couldn't get an answer from qBittorrent about where it saves the \"tv-deluno\" category, so Weir can't verify those downloads land inside the watched folder."),
            Lines(deluno));
        Assert.True(deluno["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Deluno_paths_are_mapped_into_weirs_view_before_they_are_compared()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson(
            "lib-tv",
            "/mnt/nas/complete/tv",
            "/mnt/nas/weir/tv",
            [new("qBittorrent", "tv-deluno", "/mnt/nas/complete/tv")],
            ("/mnt/nas", "/media/downloads"),
            ("/mnt", "/somewhere-else")));

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        Assert.Equal(
            [
                ("ok", $"{label}'s TV library finishes downloads in /mnt/nas/complete/tv (Weir sees it as {Watched}), inside this workflow's watched folder."),
                ("ok", $"{label}'s qBittorrent saves the \"tv-deluno\" category in /mnt/nas/complete/tv (Weir sees it as {Watched}), inside this workflow's watched folder."),
                ("ok", $"{label} picks up cleaned files from /mnt/nas/weir/tv (Weir sees it as {Output}), the folder this workflow writes to."),
            ],
            Lines(deluno));
        Assert.Equal(Watched, deluno["suggested_watched_folder"]!.GetValue<string>());
        Assert.Equal(Output, deluno["suggested_output_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_folder_outside_the_watched_folder_with_no_mappings_says_deluno_has_none_and_names_the_fix()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoWith(manager, DestinationsJson("lib-tv", "/mnt/nas/complete/tv", Output, []));

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        Assert.False(deluno["ready"]!.GetValue<bool>());
        Assert.Contains(
            (
                "problem",
                $"{label}'s TV library finishes downloads in /mnt/nas/complete/tv, which isn't inside this workflow's watched folder {Watched}. " +
                $"{label} has no path mappings for Weir. Add a path mapping from /mnt/nas to /media/downloads in {label} " +
                "(Settings › Media Management › Processing Workflow › Weir › Path mappings), or set the watched folder to /mnt/nas/complete/tv."),
            Lines(deluno));
    }

    [Fact]
    public async Task A_deluno_older_than_the_route_keeps_the_not_verified_lines_and_names_the_release_that_lets_weir_check()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoManifest(manager).Json(HttpMethod.Get, DestinationsPath, "{}", HttpStatusCode.NotFound);

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        var lines = Lines(deluno);
        Assert.Contains(
            ("unverified", $"{label} says its TV library downloads to {Watched} (inside Weir's watched folder). Weir can't see where each download client really saves."),
            lines);
        Assert.Contains(("unverified", $"{label}'s qBittorrent files this workflow's downloads under the category \"tv-deluno\", but {label} does not publish where it saves them. Weir cannot verify they land inside the watched folder."), lines);
        Assert.Equal(
            ("note", "Deluno 1.0.0-rc.23 or later lets Weir check where its download clients save and how its path mappings apply."),
            lines[^1]);
        Assert.True(deluno["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_deluno_that_answers_library_not_found_is_said_to_have_removed_the_library_and_is_never_called_too_old()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoManifest(manager).Json(
            HttpMethod.Get, DestinationsPath, """{"error":"library_not_found","message":"Deluno has no library with the id lib-tv."}""", HttpStatusCode.NotFound);

        var deluno = await DelunoCheckAsync(client);

        var label = LabelOf(deluno);
        Assert.Equal(
            [
                ("unverified", $"{label} no longer has the TV library this workflow came from, so Weir can't check where its downloads go. " +
                    $"Remove this workflow if the library is gone for good, or unlink it from {label} to keep it as a Weir-only workflow."),
            ],
            Lines(deluno));
        Assert.True(deluno["ready"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"error":"handoff_not_found"}""")]
    [InlineData("""{"error":"Library not found!"}""")]
    public async Task A_404_without_the_library_not_found_code_is_still_a_deluno_older_than_the_route(string body)
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoManifest(manager).Json(HttpMethod.Get, DestinationsPath, body, HttpStatusCode.NotFound);

        var deluno = await DelunoCheckAsync(client);

        Assert.Equal(
            ("note", "Deluno 1.0.0-rc.23 or later lets Weir check where its download clients save and how its path mappings apply."),
            Lines(deluno)[^1]);
        Assert.DoesNotContain(Lines(deluno), line => line.Text.Contains("no longer has", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_without_the_imports_scope_is_told_how_to_fix_it_and_the_check_stays_not_verified()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoManifest(manager).Json(HttpMethod.Get, DestinationsPath, """{"message":"This API key needs the imports scope."}""", HttpStatusCode.Forbidden);

        var deluno = await DelunoCheckAsync(client);

        var (state, text) = Lines(deluno)[0];
        Assert.Equal("unverified", state);
        Assert.Equal(
            $"The API key Weir uses for {LabelOf(deluno)} can't read where downloads go. Give it the Imports permission: in Deluno, open System › API Access " +
            "and create a key with Media automation access (it includes Imports), then save that key under Setup › Connections › Media managers in Weir.",
            text);
        Assert.DoesNotContain(Lines(deluno), line => line.State == "problem");
        Assert.True(deluno["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_deluno_that_does_not_answer_the_destinations_read_says_so_and_keeps_the_manifest_lines()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectAsync(client);
        DelunoManifest(manager);

        var deluno = await DelunoCheckAsync(client);

        var lines = Lines(deluno);
        var (state, text) = lines[^1];
        Assert.Equal("unverified", state);
        Assert.Equal($"Weir could not reach {LabelOf(deluno)} at http://192.0.2.10:5099. Check the address is right, and that the app is running and reachable from this machine.", text);
        Assert.Contains(lines, line => line.Text.Contains("says its TV library downloads to", StringComparison.Ordinal));
        Assert.True(deluno["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_workflow_created_from_a_deluno_library_is_checked_against_that_library()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await ConnectAsync(client);
        var folders = new DiscoveredFolders();
        try
        {
            manager.Json(HttpMethod.Get, ManifestPath, ManifestJson(
                new ManifestLibrary("lib-a", "TV A", folders.DownloadsA, folders.OutputA),
                new ManifestLibrary("lib-b", "TV B", folders.DownloadsB, folders.OutputB)));
            manager.Json(HttpMethod.Get, DestinationsPath, DestinationsJson(
                "lib-b", folders.DownloadsB, folders.OutputB, [new("qBittorrent", "tv-deluno", folders.DownloadsB)]));
            var libraryId = await ImportAsync(client, connectionId, "lib-b");

            var chain = await FolderChainAsync(client, libraryId);

            var deluno = Assert.Single(chain["managers"]!.AsArray())!;
            Assert.Equal("?libraryId=lib-b", Assert.Single(DestinationRequests(manager)).RequestUri!.Query);
            Assert.All(Lines(deluno), line => Assert.Equal("ok", line.State));
            Assert.Contains(Lines(deluno), line => line.Text.Contains($"TV library finishes downloads in {folders.DownloadsB}", StringComparison.Ordinal));
        }
        finally
        {
            folders.Dispose();
        }
    }

    [Fact]
    public async Task A_workflow_whose_deluno_library_is_gone_says_so_and_does_not_borrow_another_librarys_folders()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await ConnectAsync(client);
        var folders = new DiscoveredFolders();
        try
        {
            manager.Json(HttpMethod.Get, ManifestPath, ManifestJson(new ManifestLibrary("lib-b", "TV B", folders.DownloadsB, folders.OutputB)));
            var libraryId = await ImportAsync(client, connectionId, "lib-b");
            manager.Json(HttpMethod.Get, ManifestPath, ManifestJson(new ManifestLibrary("lib-a", "TV A", folders.DownloadsA, folders.OutputA)));

            var chain = await FolderChainAsync(client, libraryId);

            var deluno = Assert.Single(chain["managers"]!.AsArray())!;
            var (state, text) = Assert.Single(Lines(deluno));
            Assert.Equal("unverified", state);
            Assert.Equal(
                $"{LabelOf(deluno)} no longer has the library this workflow came from, so Weir can't check where its downloads go. " +
                $"Remove this workflow if the library is gone for good, or unlink it from {LabelOf(deluno)} to keep it as a Weir-only workflow.",
                text);
            Assert.Empty(DestinationRequests(manager));
        }
        finally
        {
            folders.Dispose();
        }
    }

    private static async Task<long> ImportAsync(ApiTestClient client, long connectionId, string key)
    {
        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/discover/{connectionId}/import", new { csrf_token = await client.CsrfAsync(), keys = new[] { key } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!.AsArray().Single()!["id"]!.GetValue<long>();
    }

    private static async Task<JsonNode> FolderChainAsync(ApiTestClient client, long libraryId)
    {
        using var response = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}/folder-chain");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))!;
    }

    /// <summary>Real folders for two Deluno libraries, so an imported workflow can be given them as its watched and output folders.</summary>
    private sealed class DiscoveredFolders : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("weir-deluno-destinations-").FullName;

        public DiscoveredFolders()
        {
            foreach (var folder in new[] { DownloadsA, OutputA, DownloadsB, OutputB })
            {
                Directory.CreateDirectory(folder);
            }
        }

        public string DownloadsA => Path.Join(_root, "downloads-a");

        public string OutputA => Path.Join(_root, "output-a");

        public string DownloadsB => Path.Join(_root, "downloads-b");

        public string OutputB => Path.Join(_root, "output-b");

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
