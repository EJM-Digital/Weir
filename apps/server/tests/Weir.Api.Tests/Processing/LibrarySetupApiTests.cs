using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.MediaManagers;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// First-run setup's two read-only endpoints over real HTTP: <c>GET /processing/library-suggestions</c> (the libraries offered
/// from what is connected) and <c>POST /processing/library-check</c> (the folders a person confirmed, checked before any
/// library exists), against scripted managers and download clients.
/// </summary>
public sealed class LibrarySetupApiTests
{
    private const string Suggestions = "/api/v1/processing/library-suggestions";
    private const string Check = "/api/v1/processing/library-check";

    private const string DelunoManifest = """
        {"product":"Deluno","version":"v1","instanceName":"Deluno","capabilities":["movies","tv","pre-import-processing"],
         "libraries":[{"id":"lib-movies","name":"Films","mediaType":"movie","rootPath":"/media/movies","downloadsPath":"/media/downloads/complete/movies",
                       "importWorkflow":"refine-before-import","processorOutputPath":"/media/downloads/weir/movies","processorTimeoutMinutes":0,
                       "processorFailureMode":"import-original","missingSearchEnabled":true,"upgradeSearchEnabled":true,"maxItemsPerRun":10,
                       "automationStatus":"idle","cleanupMode":"keep-source","removeEmptySourceFolders":false},
                      {"id":"lib-tv","name":"Shows","mediaType":"tv","rootPath":"/media/tv","downloadsPath":"/media/downloads/complete/tv",
                       "importWorkflow":"refine-before-import","processorOutputPath":"","processorTimeoutMinutes":0,
                       "processorFailureMode":"import-original","missingSearchEnabled":true,"upgradeSearchEnabled":true,"maxItemsPerRun":10,
                       "automationStatus":"idle","cleanupMode":"keep-source","removeEmptySourceFolders":false}],
         "indexers":[],"downloadClients":[],"connections":[]}
        """;

    private static string ArrClients(string directoryField, string directory) => $$"""
        [{"enable":true,"protocol":"torrent","name":"qBittorrent",
          "fields":[{"name":"host","value":"qbittorrent"},{"name":"{{directoryField}}","value":"{{directory}}"}],
          "implementationName":"qBittorrent","implementation":"QBittorrent","id":1}]
        """;

    private const string ArrClientsWithoutDirectory = """
        [{"enable":true,"protocol":"torrent","name":"qBittorrent","fields":[{"name":"host","value":"qbittorrent"}],
          "implementationName":"qBittorrent","implementation":"QBittorrent","id":1}]
        """;

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

    private static async Task<long> ConnectAsync(ApiTestClient client, string route, string kind, string baseUrl)
    {
        using var response = await client.PostAsync($"/api/v1/{route}/connections", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = kind,
            ["base_url"] = baseUrl,
            ["api_key"] = "key",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!["id"]!.GetValue<long>();
    }

    private static Task<long> ConnectManagerAsync(ApiTestClient client, string kind, string baseUrl) =>
        ConnectAsync(client, "media-managers", kind, baseUrl);

    private static Task<long> ConnectDownloadClientAsync(ApiTestClient client, string kind, string baseUrl) =>
        ConnectAsync(client, "download-clients", kind, baseUrl);

    private static async Task<JsonNode> SuggestedAsync(ApiTestClient client)
    {
        using var response = await client.GetAsync(Suggestions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))!;
    }

    private static JsonNode LibraryFor(JsonNode suggested, string mediaType) =>
        Assert.Single(suggested["libraries"]!.AsArray(), library => library!["media_type"]!.GetValue<string>() == mediaType)!;

    private static async Task<JsonNode> CheckAsync(ApiTestClient client, IReadOnlyDictionary<string, string> folders)
    {
        var body = new Dictionary<string, object?>(folders.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)))
        {
            ["csrf_token"] = await client.CsrfAsync(),
        };
        using var response = await client.PostAsync(Check, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))!;
    }

    private sealed record TempFolders(string Root) : IDisposable
    {
        public static TempFolders Create() => new(Path.Join(Path.GetTempPath(), "weir-library-setup-" + Guid.NewGuid().ToString("N")));

        public string Folder(string name)
        {
            var path = Path.Join(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Nothing_is_suggested_while_nothing_is_connected()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        var suggested = await SuggestedAsync(client);

        Assert.Empty(suggested["libraries"]!.AsArray());
        Assert.Empty(suggested["notes"]!.AsArray());
    }

    [Fact]
    public async Task Deluno_suggests_its_own_folders_and_a_default_output_where_it_names_none()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var delunoId = await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(HttpMethod.Get, "/api/integrations/external/manifest", DelunoManifest);

        var suggested = await SuggestedAsync(client);

        var movies = LibraryFor(suggested, "movie");
        Assert.Equal("Movies", movies["name"]!.GetValue<string>());
        Assert.Equal("/media/downloads/complete/movies", movies["watched_folder"]!.GetValue<string>());
        Assert.Equal("/media/downloads/weir/movies", movies["output_folder"]!.GetValue<string>());
        Assert.Equal("Deluno on 192.0.2.10", movies["source_label"]!.GetValue<string>());
        Assert.Equal([delunoId], movies["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
        var tv = LibraryFor(suggested, "tv");
        Assert.Equal("/media/downloads/complete/tv", tv["watched_folder"]!.GetValue<string>());
        Assert.Equal("/media/downloads/complete/Weir Ready/TV", tv["output_folder"]!.GetValue<string>());
        Assert.All(manager.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Sonarr_and_radarr_each_suggest_their_own_media_type_and_link_only_that_manager()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var sonarrId = await ConnectManagerAsync(client, "sonarr", "http://192.0.2.60:8989");
        var radarrId = await ConnectManagerAsync(client, "radarr", "http://192.0.2.61:7878");
        manager.Route(HttpMethod.Get, "/api/v3/downloadclient", request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "192.0.2.60"
                ? ArrClients("tvDirectory", "/downloads/tv")
                : ArrClients("movieDirectory", "/downloads/movies")),
        }));

        var suggested = await SuggestedAsync(client);

        var movies = LibraryFor(suggested, "movie");
        Assert.Equal("/downloads/movies", movies["watched_folder"]!.GetValue<string>());
        Assert.Equal("/downloads/Weir Ready/Movies", movies["output_folder"]!.GetValue<string>());
        Assert.Equal("Radarr on 192.0.2.61", movies["source_label"]!.GetValue<string>());
        Assert.Equal([radarrId], movies["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
        var tv = LibraryFor(suggested, "tv");
        Assert.Equal("/downloads/tv", tv["watched_folder"]!.GetValue<string>());
        Assert.Equal([sonarrId], tv["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
    }

    [Fact]
    public async Task A_downloads_folder_with_no_folder_above_it_is_offered_with_no_output_for_the_person_to_choose()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectManagerAsync(client, "radarr", "http://192.0.2.61:7878");
        manager.Json(HttpMethod.Get, "/api/v3/downloadclient", ArrClients("movieDirectory", "/movies"));

        var suggested = await SuggestedAsync(client);

        var movies = LibraryFor(suggested, "movie");
        Assert.Equal("/movies", movies["watched_folder"]!.GetValue<string>());
        Assert.Equal(string.Empty, movies["output_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_manager_that_names_no_download_folder_says_so_and_a_connected_download_client_fills_in()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var radarrId = await ConnectManagerAsync(client, "radarr", "http://192.0.2.61:7878");
        manager.Json(HttpMethod.Get, "/api/v3/downloadclient", ArrClientsWithoutDirectory);

        var alone = await SuggestedAsync(client);

        Assert.Empty(alone["libraries"]!.AsArray());
        Assert.Equal(["Radarr on 192.0.2.61 does not say where its downloads are saved."], alone["notes"]!.AsArray().Select(note => note!.GetValue<string>()));

        await ConnectDownloadClientAsync(client, "sabnzbd", "http://192.0.2.40:8080");
        manager.Json(HttpMethod.Get, "/api", """{"config":{"misc":{"complete_dir":"/downloads/complete"},"categories":[{"name":"movies","dir":"movies"}]}}""");

        var together = await SuggestedAsync(client);

        var movies = LibraryFor(together, "movie");
        Assert.Equal("/downloads/complete/movies", movies["watched_folder"]!.GetValue<string>());
        Assert.Equal("SABnzbd on 192.0.2.40", movies["source_label"]!.GetValue<string>());
        Assert.Equal([radarrId], movies["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
    }

    [Fact]
    public async Task A_download_client_offers_the_folder_of_the_category_named_for_each_media_type()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectDownloadClientAsync(client, "sabnzbd", "http://192.0.2.40:8080");
        manager.Json(
            HttpMethod.Get,
            "/api",
            """{"config":{"misc":{"complete_dir":"/downloads/complete"},"categories":[{"name":"movies","dir":"movies"},{"name":"tv","dir":"tv"}]}}""");

        var suggested = await SuggestedAsync(client);

        Assert.Equal("/downloads/complete/movies", LibraryFor(suggested, "movie")["watched_folder"]!.GetValue<string>());
        Assert.Equal("/downloads/complete/tv", LibraryFor(suggested, "tv")["watched_folder"]!.GetValue<string>());
        Assert.Empty(LibraryFor(suggested, "movie")["manager_connection_ids"]!.AsArray());
    }

    [Fact]
    public async Task A_download_clients_default_folder_is_never_offered_because_every_media_type_saves_to_it()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectDownloadClientAsync(client, "sabnzbd", "http://192.0.2.40:8080");
        manager.Json(HttpMethod.Get, "/api", """{"config":{"misc":{"complete_dir":"/downloads/complete"},"categories":[]}}""");

        var suggested = await SuggestedAsync(client);

        Assert.Empty(suggested["libraries"]!.AsArray());
        Assert.Equal(
            [
                "SABnzbd on 192.0.2.40 has no category folder for Movies, so it saves them with every other download and Weir can't tell them apart. " +
                "Give Movies its own category folder in SABnzbd on 192.0.2.40, then add the workflow under Setup › Workflows.",
                "SABnzbd on 192.0.2.40 has no category folder for TV, so it saves them with every other download and Weir can't tell them apart. " +
                "Give TV its own category folder in SABnzbd on 192.0.2.40, then add the workflow under Setup › Workflows.",
            ],
            suggested["notes"]!.AsArray().Select(note => note!.GetValue<string>()));
    }

    [Fact]
    public async Task A_Deluno_with_no_downloads_folders_and_a_client_saving_everything_to_one_folder_offers_nothing_and_says_what_to_set()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(
            HttpMethod.Get,
            "/api/integrations/external/manifest",
            DelunoManifest
                .Replace("\"downloadsPath\":\"/media/downloads/complete/movies\"", "\"downloadsPath\":\"\"", StringComparison.Ordinal)
                .Replace("\"downloadsPath\":\"/media/downloads/complete/tv\"", "\"downloadsPath\":\"\"", StringComparison.Ordinal));
        await ConnectDownloadClientAsync(client, "sabnzbd", "http://192.0.2.40:8080");
        manager.Json(HttpMethod.Get, "/api", """{"config":{"misc":{"complete_dir":"/downloads/complete"},"categories":[]}}""");

        var suggested = await SuggestedAsync(client);

        Assert.Empty(suggested["libraries"]!.AsArray());
        var notes = suggested["notes"]!.AsArray().Select(note => note!.GetValue<string>()).ToList();
        Assert.Equal(2, notes.Count(note => note.Contains("Set the downloads folder in Deluno on 192.0.2.10", StringComparison.Ordinal)));
        Assert.Equal(2, notes.Count(note => note.Contains("Give", StringComparison.Ordinal) && note.Contains("its own category folder in SABnzbd on 192.0.2.40", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Two_libraries_whose_downloads_arrive_in_the_same_folder_are_offered_as_one_with_a_sentence_saying_why()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(
            HttpMethod.Get,
            "/api/integrations/external/manifest",
            DelunoManifest.Replace("/media/downloads/complete/tv", "/media/downloads/complete/movies", StringComparison.Ordinal));

        var suggested = await SuggestedAsync(client);

        Assert.Equal("movie", Assert.Single(suggested["libraries"]!.AsArray())!["media_type"]!.GetValue<string>());
        Assert.Contains("saves Movies and TV downloads in the same folder, or one inside the other", Assert.Single(suggested["notes"]!.AsArray())!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_a_saved_workflow_already_watches_is_not_offered_again()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();
        var watched = folders.Folder("downloads");
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(
            HttpMethod.Get,
            "/api/integrations/external/manifest",
            DelunoManifest.Replace("/media/downloads/complete/movies", System.Text.Json.JsonEncodedText.Encode(watched).ToString(), StringComparison.Ordinal));
        using (var created = await client.PostAsync("/api/v1/processing/libraries", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["name"] = "Films",
            ["media_type"] = "movie",
            ["watched_folder"] = watched,
            ["output_folder"] = folders.Folder("films-out"),
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var suggested = await SuggestedAsync(client);

        Assert.Equal("tv", Assert.Single(suggested["libraries"]!.AsArray())!["media_type"]!.GetValue<string>());
        var note = Assert.Single(suggested["notes"]!.AsArray())!.GetValue<string>();
        Assert.Contains("Films already watches", note, StringComparison.Ordinal);
        Assert.Contains("so there is nothing to add", note, StringComparison.Ordinal);
        Assert.DoesNotContain("overlap", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_inside_one_a_saved_workflow_of_another_media_type_watches_is_called_an_overlap()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();
        var watched = folders.Folder("downloads");
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(
            HttpMethod.Get,
            "/api/integrations/external/manifest",
            DelunoManifest.Replace("/media/downloads/complete/movies", System.Text.Json.JsonEncodedText.Encode(watched).ToString(), StringComparison.Ordinal));
        using (var created = await client.PostAsync("/api/v1/processing/libraries", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["name"] = "Shows",
            ["media_type"] = "tv",
            ["watched_folder"] = watched,
            ["output_folder"] = folders.Folder("shows-out"),
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var suggested = await SuggestedAsync(client);

        Assert.Contains("which overlaps the", Assert.Single(suggested["notes"]!.AsArray(), note => note!.GetValue<string>().StartsWith("Shows already watches", StringComparison.Ordinal))!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_is_named_in_a_note()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        await ConnectManagerAsync(client, "sonarr", "http://192.0.2.60:8989");

        var suggested = await SuggestedAsync(client);

        Assert.Empty(suggested["libraries"]!.AsArray());
        Assert.Contains("Sonarr", Assert.Single(suggested["notes"]!.AsArray())!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_installs_empty_libraries_are_the_ones_a_suggestion_fills_in()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(HttpMethod.Get, "/api/integrations/external/manifest", DelunoManifest);

        var suggested = await SuggestedAsync(client);

        Assert.Equal(await SavedLibraryIdAsync(client, "Movies"), LibraryFor(suggested, "movie")["library_id"]!.GetValue<long>());
        Assert.Equal(await SavedLibraryIdAsync(client, "TV"), LibraryFor(suggested, "tv")["library_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_media_type_whose_first_library_is_already_in_use_gets_a_new_library_with_a_free_name()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();
        await ConnectManagerAsync(client, "deluno", "http://192.0.2.10:5099");
        manager.Json(HttpMethod.Get, "/api/integrations/external/manifest", DelunoManifest);
        using (var updated = await client.PutAsync($"/api/v1/processing/libraries/{await SavedLibraryIdAsync(client, "Movies")}", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["name"] = "Movies",
            ["media_type"] = "movie",
            ["watched_folder"] = folders.Folder("movies-in"),
            ["output_folder"] = folders.Folder("movies-out"),
        }))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        var movies = LibraryFor(await SuggestedAsync(client), "movie");

        Assert.Null(movies["library_id"]);
        Assert.Equal("Movies (2)", movies["name"]!.GetValue<string>());
    }

    private static async Task<long> SavedLibraryIdAsync(ApiTestClient client, string name)
    {
        using var response = await client.GetAsync("/api/v1/processing/libraries");
        return (await Json(response))!.AsArray().Single(library => library!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<long>();
    }

    [Fact]
    public async Task Suggestions_need_an_operator_session()
    {
        var (server, _, _) = await StartAsync();
        await using var _server = server;
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiTestClient(server).GetAsync(Suggestions)).StatusCode);

        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);

        using var response = await viewer.GetAsync(Suggestions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Folders_that_exist_and_do_not_overlap_pass_with_a_chain_and_no_problem()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();

        var result = await CheckAsync(client, new Dictionary<string, string>
        {
            ["movie_watched_folder"] = folders.Folder("movies-in"),
            ["movie_output_folder"] = folders.Folder("movies-out"),
        });

        var movie = result["movie"]!;
        Assert.Null(movie["problem"]);
        Assert.True(movie["chain"]!["local"]!["ready"]!.GetValue<bool>());
        Assert.Null(result["tv"]);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_yet_is_a_problem_in_the_chain_and_not_a_refusal()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();

        var result = await CheckAsync(client, new Dictionary<string, string>
        {
            ["movie_watched_folder"] = Path.Join(folders.Root, "not-made-yet"),
            ["movie_output_folder"] = folders.Folder("movies-out"),
        });

        var movie = result["movie"]!;
        Assert.Null(movie["problem"]);
        Assert.False(movie["chain"]!["ready"]!.GetValue<bool>());
        Assert.Contains(
            movie["chain"]!["local"]!["lines"]!.AsArray(),
            line => line!["state"]!.GetValue<string>() == "problem" && line["text"]!.GetValue<string>().Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Weirs_own_data_folder_is_refused_with_the_same_sentence_creating_a_library_gives()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();

        var result = await CheckAsync(client, new Dictionary<string, string>
        {
            ["tv_watched_folder"] = server.Home,
            ["tv_output_folder"] = folders.Folder("tv-out"),
        });

        var tv = result["tv"]!;
        Assert.Equal("Weir can't use its own data folder as a watched folder. Choose a different folder.", tv["problem"]!.GetValue<string>());
        Assert.Null(tv["chain"]);
    }

    [Fact]
    public async Task A_watched_folder_with_no_output_folder_is_refused()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();

        var result = await CheckAsync(client, new Dictionary<string, string> { ["movie_watched_folder"] = folders.Folder("movies-in") });

        Assert.Equal(
            "Set an output folder as well as a watched folder, so processed files have somewhere to go.",
            result["movie"]!["problem"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_proposed_libraries_that_share_a_folder_are_each_told_so()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();
        var shared = folders.Folder("downloads");

        var result = await CheckAsync(client, new Dictionary<string, string>
        {
            ["movie_watched_folder"] = shared,
            ["movie_output_folder"] = folders.Folder("movies-out"),
            ["tv_watched_folder"] = shared,
            ["tv_output_folder"] = folders.Folder("tv-out"),
        });

        Assert.Contains("overlaps the watched folder of 'TV'", result["movie"]!["problem"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("overlaps the watched folder of 'Movies'", result["tv"]!["problem"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposed_folder_that_overlaps_a_saved_library_is_refused()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();
        var watched = folders.Folder("movies-in");
        using (var created = await client.PostAsync("/api/v1/processing/libraries", new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["name"] = "Films",
            ["media_type"] = "movie",
            ["watched_folder"] = watched,
            ["output_folder"] = folders.Folder("films-out"),
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var result = await CheckAsync(client, new Dictionary<string, string>
        {
            ["tv_watched_folder"] = watched,
            ["tv_output_folder"] = folders.Folder("tv-out"),
        });

        Assert.Contains("overlaps the watched folder of 'Films'", result["tv"]!["problem"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_folders_saves_no_library()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        using var folders = TempFolders.Create();

        var movies = await SavedLibraryIdAsync(client, "Movies");

        await CheckAsync(client, new Dictionary<string, string>
        {
            ["movie_watched_folder"] = folders.Folder("movies-in"),
            ["movie_output_folder"] = folders.Folder("movies-out"),
        });

        using var listed = await client.GetAsync("/api/v1/processing/libraries");
        var saved = (await Json(listed))!.AsArray().Single(library => library!["id"]!.GetValue<long>() == movies)!;
        Assert.Equal(string.Empty, saved["watched_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_check_needs_the_confirmation_token()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var response = await client.PostAsync(Check, new Dictionary<string, object?> { ["csrf_token"] = "wrong" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
