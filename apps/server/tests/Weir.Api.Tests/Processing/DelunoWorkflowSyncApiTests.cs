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
/// Connecting Deluno sets up Weir's workflows from it with no typing: over real HTTP against a scripted Deluno that, like a
/// fresh install, has no downloads folder of its own and says where its clients save instead.
/// </summary>
public sealed class DelunoWorkflowSyncApiTests
{
    private const string Libraries = "/api/v1/processing/libraries";
    private const string Connections = "/api/v1/media-managers/connections";
    private const string ManifestPath = "/api/integrations/external/manifest";
    private const string DestinationsPath = "/api/integrations/processors/download-destinations";

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Root = Path.Join(Path.GetTempPath(), "weir-sync-" + Guid.NewGuid().ToString("N"));
            foreach (var folder in new[] { "Completed/Movies", "Completed/TV", "Ready/Movies", "Ready/TV" })
            {
                Directory.CreateDirectory(Path.Join(Root, folder));
            }
        }

        public string Root { get; }

        public string Folder(string relative) => Path.Join(Root, relative);

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

    private static string Manifest(Rig rig) => JsonSerializer.Serialize(new
    {
        libraries = new[]
        {
            Library("lib-movies", "Movies", "movie", rig.Folder("Ready/Movies")),
            Library("lib-tv", "TV", "tv", rig.Folder("Ready/TV")),
        },
    });

    private static Dictionary<string, object?> Library(string id, string name, string mediaType, string output) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["mediaType"] = mediaType,
        ["rootPath"] = "/library/" + id,
        ["importWorkflow"] = "refine-before-import",
        ["processorOutputPath"] = output,
        ["downloadsPath"] = string.Empty,
    };

    private static string Destinations(Rig rig, string movies = "Completed/Movies", string tv = "Completed/TV") => JsonSerializer.Serialize(new
    {
        libraries = new[]
        {
            Published("lib-movies", "Movies", rig.Folder(movies), rig.Folder("Ready/Movies")),
            Published("lib-tv", "TV", rig.Folder(tv), rig.Folder("Ready/TV")),
        },
    });

    private static Dictionary<string, object?> Published(string id, string name, string saveFolder, string output) => new()
    {
        ["libraryId"] = id,
        ["libraryName"] = name,
        ["downloadsPath"] = string.Empty,
        ["processorOutputPath"] = output,
        ["destinations"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["downloadClientName"] = "qBittorrent",
                ["category"] = "weir",
                ["categoryKind"] = "category",
                ["saveFolder"] = saveFolder,
                ["savedBy"] = "client-category",
                ["status"] = "ok",
                ["message"] = string.Empty,
            },
        },
        ["processorConnection"] = new { pathMappings = Array.Empty<object>() },
    };

    private static async Task<(WeirTestServer Server, ApiTestClient Client, ScriptedManager Manager)> StartAsync(Rig rig)
    {
        var manager = new ScriptedManager()
            .Json(HttpMethod.Get, ManifestPath, Manifest(rig))
            .Json(HttpMethod.Get, DestinationsPath, Destinations(rig))
            .Json(HttpMethod.Get, "/api/integrations/external/health", "{}");
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), (WeirTestServer.ManagerWorkflowSyncVariable, "1")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client, manager);
    }

    private static async Task<long> ConnectAsync(ApiTestClient client)
    {
        using var response = await client.PostAsync(Connections, new Dictionary<string, object?>
        {
            ["csrf_token"] = await client.CsrfAsync(),
            ["kind"] = "deluno",
            ["base_url"] = "http://192.0.2.10:5099",
            ["api_key"] = "deluno-key",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))!["id"]!.GetValue<long>();
    }

    private static async Task<JsonArray> WorkflowsAsync(ApiTestClient client)
    {
        using var response = await client.GetAsync(Libraries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))!.AsArray();
    }

    /// <summary>The workflows once Deluno has been asked for them: the sync runs in the background after the connection is saved.</summary>
    private static Task<JsonArray> SyncedWorkflowsAsync(ApiTestClient client, int count = 2) =>
        Eventually.PollAsync(
            () => WorkflowsAsync(client),
            workflows => workflows.Count(workflow => workflow!["discovered_library_key"] is not null) >= count,
            TimeSpan.FromSeconds(30));

    private static JsonNode Workflow(JsonArray workflows, string mediaType) =>
        Assert.Single(workflows, workflow => workflow!["media_type"]!.GetValue<string>() == mediaType)!;

    private static async Task<JsonNode> FolderChainAsync(ApiTestClient client, JsonNode workflow)
    {
        using var response = await client.GetAsync($"{Libraries}/{workflow["id"]!.GetValue<long>()}/folder-chain");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))!;
    }

    [Fact]
    public async Task Connecting_Deluno_fills_both_workflows_links_them_and_leaves_the_folder_chain_ready_with_nothing_typed()
    {
        using var rig = new Rig();
        var (server, client, _) = await StartAsync(rig);
        await using var _server = server;

        var delunoId = await ConnectAsync(client);
        var workflows = await SyncedWorkflowsAsync(client);

        Assert.Equal(2, workflows.Count);
        foreach (var (mediaType, name, completed, ready) in new[] { ("movie", "Movies", "Completed/Movies", "Ready/Movies"), ("tv", "TV", "Completed/TV", "Ready/TV") })
        {
            var workflow = Workflow(workflows, mediaType);
            Assert.Equal(name, workflow["name"]!.GetValue<string>());
            Assert.Equal(rig.Folder(completed), workflow["watched_folder"]!.GetValue<string>());
            Assert.Equal(rig.Folder(ready), workflow["output_folder"]!.GetValue<string>());
            Assert.Equal(string.Empty, workflow["work_folder"]!.GetValue<string>());
            Assert.Equal([delunoId], workflow["manager_connection_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
            Assert.Equal(delunoId, workflow["discovered_from_connection_id"]!.GetValue<long>());
            Assert.Equal(delunoId, workflow["folders_synced_from_connection_id"]!.GetValue<long>());
            Assert.NotNull(workflow["rule_set_id"]);
            var chain = await FolderChainAsync(client, workflow);
            Assert.True(chain["ready"]!.GetValue<bool>(), chain.ToJsonString());
        }
    }

    [Fact]
    public async Task Setting_up_is_told_in_Activity_once_per_workflow()
    {
        using var rig = new Rig();
        var (server, client, _) = await StartAsync(rig);
        await using var _server = server;
        await ConnectAsync(client);
        await SyncedWorkflowsAsync(client);

        using var response = await client.GetAsync("/api/v1/activity/recent?limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = (await Json(response))!.ToJsonString();

        Assert.Contains("Workflow Movies set up from Deluno on 192.0.2.10", text, StringComparison.Ordinal);
        Assert.Contains("Workflow TV set up from Deluno on 192.0.2.10", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passing_connection_test_runs_the_sync_again()
    {
        using var rig = new Rig();
        var (server, client, manager) = await StartAsync(rig);
        await using var _server = server;
        var delunoId = await ConnectAsync(client);
        await SyncedWorkflowsAsync(client);
        manager.Json(HttpMethod.Get, DestinationsPath, Destinations(rig, "Completed/Movies 4K"));

        using var test = await client.PostAsync($"{Connections}/{delunoId}/test", new Dictionary<string, object?> { ["csrf_token"] = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);

        var expected = rig.Folder("Completed/Movies 4K");
        var workflows = await Eventually.PollAsync(
            () => WorkflowsAsync(client),
            all => Workflow(all, "movie")["watched_folder"]!.GetValue<string>() == expected,
            TimeSpan.FromSeconds(30));

        Assert.Equal(expected, Workflow(workflows, "movie")["watched_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unlinking_a_workflow_makes_its_folders_its_own_and_the_sync_leaves_it_alone()
    {
        using var rig = new Rig();
        var (server, client, manager) = await StartAsync(rig);
        await using var _server = server;
        var delunoId = await ConnectAsync(client);
        var movies = Workflow(await SyncedWorkflowsAsync(client), "movie");
        var original = movies["watched_folder"]!.GetValue<string>();
        var tvBefore = Workflow(await WorkflowsAsync(client), "tv")["watched_folder"]!.GetValue<string>();

        using (var unlinked = await client.PostAsync($"{Libraries}/{movies["id"]!.GetValue<long>()}/unlink", new Dictionary<string, object?> { ["csrf_token"] = await client.CsrfAsync() }))
        {
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
            Assert.Null((await Json(unlinked))!["folders_synced_from_connection_id"]?.GetValue<long>());
        }

        manager.Json(HttpMethod.Get, DestinationsPath, Destinations(rig, "Moved/Movies", "Moved/TV"));
        using var test = await client.PostAsync($"{Connections}/{delunoId}/test", new Dictionary<string, object?> { ["csrf_token"] = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);

        // The sync saves every workflow of the connection together, so once the linked TV workflow has moved, the pass has finished.
        var workflows = await Eventually.PollAsync(
            () => WorkflowsAsync(client),
            all => Workflow(all, "tv")["watched_folder"]!.GetValue<string>() != tvBefore,
            TimeSpan.FromSeconds(30));

        Assert.Equal(rig.Folder("Moved/TV"), Workflow(workflows, "tv")["watched_folder"]!.GetValue<string>());
        Assert.Equal(original, Workflow(workflows, "movie")["watched_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_workflow_with_no_manager_reports_its_folders_as_its_own()
    {
        using var rig = new Rig();
        var (server, client, _) = await StartAsync(rig);
        await using var _server = server;

        var workflows = await WorkflowsAsync(client);

        Assert.All(workflows, workflow => Assert.Null(workflow!["folders_synced_from_connection_id"]?.GetValue<long>()));
    }
}
