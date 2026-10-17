using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// A fake Deluno for the workflow sync tests: a Movies and a TV library set to Refine before import, no downloads folder of
/// their own, and the folders where its download clients save published under download-destinations, as a fresh install does.
/// </summary>
internal static class DelunoSyncRig
{
    public const string LibrariesRoute = $"{WeirClient.Api}/processing/libraries";
    public const string Manifest = "/api/integrations/external/manifest";
    public const string DestinationsPath = "/api/integrations/processors/download-destinations";

    private const int SyncAttempts = 6;

    public static readonly IReadOnlyDictionary<string, string> SyncOn = new Dictionary<string, string> { [ServerEnvironment.WorkflowSync] = "1" };

    public static JsonObject Library(string id, string name, string mediaType, string output, string importWorkflow = "refine-before-import") => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["mediaType"] = mediaType,
        ["rootPath"] = "/library/" + id,
        ["importWorkflow"] = importWorkflow,
        ["processorOutputPath"] = output,
        ["downloadsPath"] = string.Empty,
    };

    public static JsonObject Published(string id, string name, string saveFolder, string output) => new()
    {
        ["libraryId"] = id,
        ["libraryName"] = name,
        ["downloadsPath"] = string.Empty,
        ["processorOutputPath"] = output,
        ["destinations"] = new JsonArray(new JsonObject
        {
            ["downloadClientName"] = "qBittorrent",
            ["category"] = "weir",
            ["categoryKind"] = "category",
            ["saveFolder"] = saveFolder,
            ["savedBy"] = "client-category",
            ["status"] = "ok",
            ["message"] = string.Empty,
        }),
        ["processorConnection"] = new JsonObject { ["pathMappings"] = new JsonArray() },
    };

    public static string MoviesReady(TemporaryFolder rig) => Directory.CreateDirectory(Path.Join(rig.Path, "Ready", "Movies")).FullName;

    public static string TvReady(TemporaryFolder rig) => Directory.CreateDirectory(Path.Join(rig.Path, "Ready", "TV")).FullName;

    public static FakeManager DelunoWith(TemporaryFolder rig, string moviesSaveFolder)
    {
        var fake = FakeManager.StartDeluno([Library("lib-movies", "Movies", "movie", MoviesReady(rig)), Library("lib-tv", "TV", "tv", TvReady(rig))]);
        PublishDestinations(fake, rig, moviesSaveFolder);
        return fake;
    }

    public static void PublishDestinations(FakeManager fake, TemporaryFolder rig, string moviesSaveFolder, string tvSaveFolder = "Completed/TV") =>
        fake.Route("GET", DestinationsPath, new JsonObject
        {
            ["libraries"] = new JsonArray(
                Published("lib-movies", "Movies", Directory.CreateDirectory(Path.Join(rig.Path, moviesSaveFolder)).FullName, Path.Join(rig.Path, "Ready", "Movies")),
                Published("lib-tv", "TV", Directory.CreateDirectory(Path.Join(rig.Path, tvSaveFolder)).FullName, Path.Join(rig.Path, "Ready", "TV"))),
        });

    /// <summary>Connects <paramref name="fake"/> and returns the connection as the API reports it.</summary>
    public static async Task<JsonObject> ConnectAsync(WeirClient client, FakeManager fake, JsonObject? overrides = null)
    {
        var created = await ManagerConnections.CreateAsync(
            client, JsonFields.Merge(new JsonObject { ["base_url"] = fake.BaseUrl, ["api_key"] = fake.ApiKey }, overrides));
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return created.Fields;
    }

    /// <summary>Runs a connection test, which asks Weir to sync again when Deluno answers.</summary>
    public static async Task TestConnectionAsync(WeirClient client, JsonObject connection)
    {
        var test = await client.PostWithCsrfAsync($"{ManagerConnections.Route}/{JsonFields.Id(connection)}/test", new JsonObject());
        Assert.True(test.Status == HttpStatusCode.OK, test.ToString());
    }

    /// <summary>
    /// Tests the connection, which asks Weir to sync, and waits for <paramref name="probe"/> to find the result. One sync is one
    /// look at the manager: if it read the manager just before a change, or found it not answering, nothing looks again until the
    /// five-minute timer. So a test that waits on a sync tests the connection again if the result has not shown within a few
    /// seconds, as a person would, rather than depending on the first look having worked.
    /// </summary>
    public static async Task<T> SyncUntilAsync<T>(WeirClient client, JsonObject connection, Func<Task<T?>> probe, string what)
        where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            await TestConnectionAsync(client, connection);
            try
            {
                return await Poll.UntilAsync(probe, what, TimeSpan.FromSeconds(10));
            }
            catch (Xunit.Sdk.XunitException) when (attempt < SyncAttempts)
            {
            }
        }
    }

    public static async Task<JsonArray> SyncedAsync(WeirClient client, int expected = 2) =>
        await Poll.UntilAsync(
            async () =>
            {
                var rows = await ProcessingLibraries.ListAsync(client);
                return rows.Count(row => row!["discovered_library_key"] is not null) >= expected ? rows : null;
            },
            $"{expected} workflow(s) to be set up from Deluno");

    /// <summary>A folder under the rig, written the way this machine writes paths.</summary>
    public static string Native(TemporaryFolder rig, string relative) => Path.Join(rig.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    public static JsonObject Movies(JsonArray workflows) =>
        Assert.Single(workflows, row => (string)row!["media_type"]! == "movie")!.AsObject();

    public static async Task<JsonObject> WorkflowAsync(WeirClient client, long id)
    {
        var read = await client.GetAsync($"{LibrariesRoute}/{id}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        return read.Fields;
    }

    /// <summary>
    /// What a workflow holds, as one string to compare: every field a save takes, plus where it came from and when it was last saved.
    /// What Weir works out from the manager's state (its coverage) is left out, as it can change without the workflow being touched.
    /// </summary>
    public static string Held(JsonObject workflow)
    {
        var held = LibraryBodies.Unchanged(workflow);
        foreach (var field in new[] { "discovered_from_connection_id", "discovered_library_key", "folders_synced_from_connection_id", "updated_at" })
        {
            held[field] = workflow[field]?.DeepClone();
        }

        return held.ToJsonString();
    }

    /// <summary>Weir's own notes from the workflow sync, as Activity lists them.</summary>
    public static async Task<List<JsonObject>> SyncNoticesAsync(WeirClient client)
    {
        var events = await client.GetAsync($"{WeirClient.Api}/activity/recent", ("event_type", "processing.workflow_sync_notice"), ("limit", 100));
        Assert.True(events.Status == HttpStatusCode.OK, events.ToString());
        return [.. events.Fields["items"]!.AsArray().Select(item => item!.AsObject())];
    }

    /// <summary>The notice whose title is <paramref name="title"/>, once Weir has recorded it.</summary>
    public static Task<JsonObject> NoticeAsync(WeirClient client, string title) =>
        Poll.UntilAsync(
            async () => (await SyncNoticesAsync(client)).FirstOrDefault(item => (string?)item["title"] == title),
            $"a notice titled '{title}'");
}
