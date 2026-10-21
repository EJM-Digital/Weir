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
/// Media-manager library discovery, import, drift and unlink over real HTTP against a scripted Deluno
/// manifest (#554).
/// </summary>
public sealed class ProcessingLibraryDiscoveryApiTests
{
    private const string Connections = "/api/v1/media-managers/connections";

    private static string DiscoverPath(long connectionId) => $"/api/v1/processing/libraries/discover/{connectionId}";

    private static string ImportPath(long connectionId) => $"/api/v1/processing/libraries/discover/{connectionId}/import";

    private static string DriftPath(long connectionId) => $"/api/v1/processing/libraries/discover/{connectionId}/drift";

    private static string UnlinkPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/unlink";

    private sealed record ManifestLibrary(
        string Id,
        string Name,
        string MediaType,
        string? RootPath = null,
        string ImportWorkflow = "standard",
        string? ProcessorOutputPath = null,
        string? DownloadsPath = null);

    /// <summary>The confirmed Deluno manifest shape (Deluno#331).</summary>
    private static string ManifestJson(params ManifestLibrary[] libraries)
    {
        var payload = new
        {
            libraries = libraries.Select(library => new Dictionary<string, object?>
            {
                ["id"] = library.Id,
                ["name"] = library.Name,
                ["mediaType"] = library.MediaType,
                ["rootPath"] = library.RootPath,
                ["importWorkflow"] = library.ImportWorkflow,
                ["processorOutputPath"] = library.ProcessorOutputPath,
                ["downloadsPath"] = library.DownloadsPath,
            }),
        };
        return JsonSerializer.Serialize(payload);
    }

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

    private static async Task<long> CreateConnectionAsync(ApiTestClient client, string baseUrl = "http://192.0.2.10:5099", string? apiKey = "deluno_secret_key")
    {
        var body = new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = "deluno",
            ["name"] = "Deluno",
            ["base_url"] = baseUrl,
        };
        if (apiKey is not null)
        {
            body["api_key"] = apiKey;
        }

        using var response = await client.PostAsync(Connections, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!["id"]!.GetValue<long>();
    }

    private static void SetManifest(ScriptedManager manager, string json) =>
        manager.Json(HttpMethod.Get, "/api/integrations/external/manifest", json);

    private static async Task<JsonNode> ImportAsync(ApiTestClient client, long connectionId, params string[] keys)
    {
        using var response = await client.PostAsync(
            ImportPath(connectionId), new { csrf_token = await client.CsrfAsync(), keys });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    // --- discovery ------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_marks_what_is_already_imported()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var moviesRoot = Directory.CreateTempSubdirectory().FullName;
        var tvRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(
                new ManifestLibrary("7", "Films", "movies", moviesRoot),
                new ManifestLibrary("8", "Shows", "tv", tvRoot)));

            await ImportAsync(client, connectionId, "7");

            using var response = await client.GetAsync(DiscoverPath(connectionId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var found = (await Json(response))!.AsArray().ToDictionary(item => item!["key"]!.GetValue<string>());

            Assert.True(found["7"]!["already_imported"]!.GetValue<bool>());
            Assert.False(found["8"]!["already_imported"]!.GetValue<bool>());
            Assert.Equal("tv", found["8"]!["media_type"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(moviesRoot, recursive: true);
            Directory.Delete(tvRoot, recursive: true);
        }
    }

    [Fact]
    public async Task The_listing_shows_the_output_root_before_the_import()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Movies", "movies", root, "refine-before-import", root)));

            using var response = await client.GetAsync(DiscoverPath(connectionId));
            var found = (await Json(response))!.AsArray()[0]!;

            Assert.True(found["processes_before_import"]!.GetValue<bool>());
            Assert.Equal(root, found["output_path"]!.GetValue<string>());
            Assert.Null(found["output_path_problem"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Discover_for_an_unknown_connection_is_404()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var response = await client.GetAsync(DiscoverPath(999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("That media manager connection does not exist.", await Detail(response));
    }

    [Fact]
    public async Task A_connection_with_no_saved_key_cannot_be_asked()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client, apiKey: null);

        using var response = await client.GetAsync(DiscoverPath(connectionId));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("address and API key", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drift_for_a_connection_with_no_saved_key_cannot_be_judged()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client, apiKey: null);

        using var response = await client.GetAsync(DriftPath(connectionId));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("address and API key", await Detail(response), StringComparison.Ordinal);
    }

    // --- import -----------------------------------------------------------------------------------

    [Fact]
    public async Task Importing_a_subset_creates_only_what_was_chosen()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(
                new ManifestLibrary("7", "Films", "movies", root),
                new ManifestLibrary("8", "Shows", "tv", root),
                new ManifestLibrary("9", "Kids", "movies", root)));

            var created = (await ImportAsync(client, connectionId, "7", "9")).AsArray();

            Assert.Equal(["Films", "Kids"], created.Select(r => r!["name"]!.GetValue<string>()).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new HashSet<string> { "7", "9" }, created.Select(r => r!["discovered_library_key"]!.GetValue<string>()).ToHashSet());
            Assert.All(created, r => Assert.Equal(connectionId, r!["discovered_from_connection_id"]!.GetValue<long>()));
            // The manager's root is adopted as the watched folder when Weir can see it.
            Assert.All(created, r => Assert.Equal(root, r!["watched_folder"]!.GetValue<string>()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_imported_library_starts_at_the_default_wait_and_minimum_size_whatever_Performance_says()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));
            using var saved = await client.PutAsync(
                "/api/v1/processing/operator-settings",
                new { csrf_token = await client.CsrfAsync(), min_file_age_seconds = 15, min_input_file_size_mb = 7 });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var imported = (await ImportAsync(client, connectionId, "7")).AsArray().Single()!;

            Assert.Equal(50, imported["min_file_size_mb"]!.GetValue<long>());
            Assert.Equal(60, imported["ready_after_seconds"]!.GetValue<long>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_root_weir_cannot_see_imports_without_a_watched_folder()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", "/srv/elsewhere")));

        var created = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!;

        Assert.Equal(string.Empty, created["watched_folder"]!.GetValue<string>());
        Assert.Equal("7", created["discovered_library_key"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_duplicate_name_is_disambiguated_rather_than_refused()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using (var manual = await client.PostAsync(
                "/api/v1/processing/libraries",
                new { csrf_token = await client.CsrfAsync(), name = "Films", media_type = "movie" }))
            {
                Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
            }

            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));

            var created = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!;

            Assert.Equal("Films (2)", created["name"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Importing_nothing_is_refused_with_422()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        SetManifest(manager, ManifestJson());

        using var response = await client.PostAsync(ImportPath(connectionId), new { csrf_token = await client.CsrfAsync(), keys = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("too_short", (await Json(response))!["detail"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Importing_an_unknown_key_is_refused_with_400()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));

            using var response = await client.PostAsync(ImportPath(connectionId), new { csrf_token = await client.CsrfAsync(), keys = new[] { "missing" } });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("no longer reports a library with id missing", await Detail(response), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Importing_a_refine_before_import_library_seeds_its_output_folder()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var watched = Directory.CreateTempSubdirectory().FullName;
        var output = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Movies", "movies", watched, "refine-before-import", output)));

            var created = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!;

            Assert.Equal(watched, created["watched_folder"]!.GetValue<string>());
            Assert.Equal(output, created["output_folder"]!.GetValue<string>());
            // #651: an imported library is linked to the manager it came from, and on, since it can hand files back.
            Assert.Equal([connectionId], created["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
            Assert.True(created["enabled"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// A Refine before import library's hand-offs come from where its downloads arrive, and a hand-off must sit inside the
    /// watched folder; the library root is where finished media ends up. So the downloads folder is what is watched, and
    /// listed, whenever Deluno reports one — and drift is judged against it too.
    /// </summary>
    [Fact]
    public async Task A_refine_before_import_library_watches_where_its_downloads_arrive_not_its_library_root()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        var downloads = Directory.CreateTempSubdirectory().FullName;
        var output = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "TV", "tv", root, "refine-before-import", output, downloads)));

            using var listed = await client.GetAsync(DiscoverPath(connectionId));
            var item = (await Json(listed))!.AsArray()[0]!;
            var created = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!;
            using var drift = await client.GetAsync(DriftPath(connectionId));

            Assert.Equal(downloads, item["root_path"]!.GetValue<string>());
            Assert.Equal(downloads, created["watched_folder"]!.GetValue<string>());
            Assert.Equal(output, created["output_folder"]!.GetValue<string>());
            Assert.Empty((await Json(drift))!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(downloads, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task A_standard_library_is_imported_with_no_output_folder()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var watched = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("8", "TV", "tv", watched)));

            var created = (await ImportAsync(client, connectionId, "8")).AsArray()[0]!;

            Assert.Equal(string.Empty, created["output_folder"]!.GetValue<string>());
            // With nowhere to hand files back it arrives off, still linked to its manager.
            Assert.False(created["enabled"]!.GetValue<bool>());
            Assert.Equal([connectionId], created["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }

    [Fact]
    public async Task An_output_root_this_machine_cannot_see_is_left_empty_rather_than_saved()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var watched = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Movies", "movies", watched, "refine-before-import", "relative/not/absolute")));

            var created = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!;

            Assert.Equal(watched, created["watched_folder"]!.GetValue<string>());
            Assert.Equal(string.Empty, created["output_folder"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }

    // --- drift ------------------------------------------------------------------------------------

    [Fact]
    public async Task Resync_reports_a_moved_root_and_changes_nothing()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var oldRoot = Directory.CreateTempSubdirectory().FullName;
        var newRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", oldRoot)));
            var libraryId = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!["id"]!.GetValue<long>();

            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", newRoot)));

            using var response = await client.GetAsync(DriftPath(connectionId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var drift = (await Json(response))!.AsArray();
            var moved = drift.Single(d => d!["kind"]!.GetValue<string>() == "root_moved");
            Assert.Equal(newRoot, moved!["manager_value"]!.GetValue<string>());
            Assert.Equal(oldRoot, moved["weir_value"]!.GetValue<string>());

            using var library = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}");
            Assert.Equal(oldRoot, (await Json(library))!["watched_folder"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(oldRoot, recursive: true);
            Directory.Delete(newRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Resync_reports_a_library_the_manager_no_longer_has()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Gone", "movies", root)));
            var libraryId = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!["id"]!.GetValue<long>();

            SetManifest(manager, ManifestJson());

            using var response = await client.GetAsync(DriftPath(connectionId));
            var drift = (await Json(response))!.AsArray();

            Assert.Equal(["library_removed"], drift.Select(d => d!["kind"]!.GetValue<string>()));
            Assert.Equal(libraryId, drift[0]!["library_id"]!.GetValue<long>());

            using var library = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}");
            Assert.Equal(root, (await Json(library))!["watched_folder"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resync_reports_a_library_that_appeared()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("9", "New", "movies", root)));

            using var response = await client.GetAsync(DriftPath(connectionId));
            var drift = (await Json(response))!.AsArray();

            Assert.Equal(["library_added"], drift.Select(d => d!["kind"]!.GetValue<string>()));
            Assert.Equal("New", drift[0]!["library_name"]!.GetValue<string>());

            // Reported only — nothing was created (the server always seeds "Movies"/"TV" on a fresh database).
            using var libraries = await client.GetAsync("/api/v1/processing/libraries");
            Assert.DoesNotContain((await Json(libraries))!.AsArray(), lib => lib!["name"]!.GetValue<string>() == "New");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resync_is_quiet_when_nothing_has_changed()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));
            await ImportAsync(client, connectionId, "7");

            using var response = await client.GetAsync(DriftPath(connectionId));

            Assert.Empty((await Json(response))!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_manual_library_is_never_reported_as_drift()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        using (var manual = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Hand made", media_type = "movie" }))
        {
            Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        }

        SetManifest(manager, ManifestJson());

        using var response = await client.GetAsync(DriftPath(connectionId));

        Assert.Empty((await Json(response))!.AsArray());
    }

    // --- unlink -----------------------------------------------------------------------------------

    [Fact]
    public async Task Unlinking_keeps_the_library()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));
            var libraryId = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!["id"]!.GetValue<long>();

            using var response = await client.PostAsync(UnlinkPath(libraryId), new { csrf_token = await client.CsrfAsync() });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await Json(response);
            Assert.Equal("Films", body!["name"]!.GetValue<string>());
            Assert.Equal(root, body["watched_folder"]!.GetValue<string>());
            Assert.Null(body["discovered_from_connection_id"]);
            Assert.Null(body["discovered_library_key"]);

            using var reloaded = await client.GetAsync($"/api/v1/processing/libraries/{libraryId}");
            var reloadedBody = await Json(reloaded);
            Assert.Null(reloadedBody!["discovered_from_connection_id"]);
            Assert.Null(reloadedBody["discovered_library_key"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unlinking_an_unknown_library_is_404()
    {
        var (server, client, _) = await StartAsync();
        await using var _server = server;

        using var response = await client.PostAsync(UnlinkPath(999), new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("That workflow does not exist.", await Detail(response));
    }

    [Fact]
    public async Task Unlinking_with_a_bad_csrf_token_is_refused()
    {
        var (server, client, manager) = await StartAsync();
        await using var _server = server;
        var connectionId = await CreateConnectionAsync(client);
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            SetManifest(manager, ManifestJson(new ManifestLibrary("7", "Films", "movies", root)));
            var libraryId = (await ImportAsync(client, connectionId, "7")).AsArray()[0]!["id"]!.GetValue<long>();

            using var response = await client.PostAsync(UnlinkPath(libraryId), new { csrf_token = "bad" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(response));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
