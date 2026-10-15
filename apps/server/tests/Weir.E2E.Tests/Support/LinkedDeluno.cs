using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// Weir connected to a fake Deluno, which has set up the Movies workflow from its own folders. Disposing it takes the connection away
/// again the way a person has to (unlink the workflows, then remove it), unless the test already removed it.
/// </summary>
public sealed class LinkedDeluno : IAsyncDisposable
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string Connections = $"{WeirClient.Api}/media-managers/connections";
    private const string DestinationsPath = "/api/integrations/processors/download-destinations";

    private LinkedDeluno(WeirClient client, FakeManager deluno, long delunoId, string name, string saveFolder, string readyFolder)
    {
        Client = client;
        Deluno = deluno;
        DelunoId = delunoId;
        Name = name;
        SaveFolder = saveFolder;
        ReadyFolder = readyFolder;
    }

    public WeirClient Client { get; }

    public FakeManager Deluno { get; }

    public long DelunoId { get; }

    /// <summary>What the connection is called: its kind and the host in its address.</summary>
    public string Name { get; }

    /// <summary>Where Deluno's download client saves finished Movies downloads.</summary>
    public string SaveFolder { get; }

    /// <summary>Where Deluno takes processed Movies back from.</summary>
    public string ReadyFolder { get; }

    /// <summary>The Movies workflow Deluno's folders set up.</summary>
    public long MoviesId { get; private set; }

    public static async Task<LinkedDeluno> StartAsync(string baseUrl, TemporaryFolder rig)
    {
        var saveFolder = Directory.CreateDirectory(Path.Join(rig.Path, "Completed", "Movies")).FullName;
        var readyFolder = Directory.CreateDirectory(Path.Join(rig.Path, "Ready", "Movies")).FullName;
        var deluno = FakeManager.StartDeluno([new JsonObject
        {
            ["id"] = "lib-movies",
            ["name"] = "Movies",
            ["mediaType"] = "movie",
            ["rootPath"] = "/library/lib-movies",
            ["importWorkflow"] = "refine-before-import",
            ["processorOutputPath"] = readyFolder,
            ["downloadsPath"] = string.Empty,
        }]);
        deluno.Route("GET", DestinationsPath, new JsonObject
        {
            ["libraries"] = new JsonArray(new JsonObject
            {
                ["libraryId"] = "lib-movies",
                ["libraryName"] = "Movies",
                ["downloadsPath"] = string.Empty,
                ["processorOutputPath"] = readyFolder,
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
            }),
        });
        var client = await AdminSession.OpenAsync(baseUrl);
        var created = await client.PostWithCsrfAsync(Connections, new JsonObject
        {
            ["kind"] = "deluno",
            ["base_url"] = deluno.BaseUrl,
            ["api_key"] = deluno.ApiKey,
            ["enabled"] = true,
        });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var delunoId = (long)created.Fields["id"]!;
        var linked = new LinkedDeluno(client, deluno, delunoId, (string)created.Fields["name"]!, saveFolder, readyFolder);
        try
        {
            var movies = await Poll.UntilAsync(
                async () =>
                {
                    var listed = await client.GetAsync(Libraries);
                    Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
                    return listed.Elements
                        .Select(row => row!.AsObject())
                        .FirstOrDefault(row => (long?)row["folders_synced_from_connection_id"] == delunoId && (string)row["media_type"]! == "movie");
                },
                "the Movies workflow to be set up from Deluno");
            linked.MoviesId = (long)movies["id"]!;
            return linked;
        }
        catch
        {
            await linked.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var connections = await Client.GetAsync(Connections);
            if (connections.Elements.Any(connection => (long?)connection!["id"] == DelunoId))
            {
                await ManagerConnections.RemoveAsync(Client, DelunoId);
            }
        }
        finally
        {
            Client.Dispose();
            Deluno.Dispose();
        }
    }
}
