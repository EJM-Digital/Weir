using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A TV workflow linked to a Sonarr whose qBittorrent download client Weir also knows: the setup the editor reads, with Sonarr saying
/// which client it uses and where that client saves finished downloads. Disposing it takes everything away again.
/// </summary>
public sealed class SonarrFedWorkflow : IAsyncDisposable
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string QBittorrentUser = "weir-qbit";
    private const string QBittorrentSecret = "qbit-password";

    private SonarrFedWorkflow(WeirClient admin, FakeManager sonarr, FakeDownloadClient client, long sonarrId, long clientId, long workflowId, string name)
    {
        Admin = admin;
        Sonarr = sonarr;
        Client = client;
        SonarrId = sonarrId;
        ClientId = clientId;
        WorkflowId = workflowId;
        Name = name;
    }

    public WeirClient Admin { get; }

    public FakeManager Sonarr { get; }

    public FakeDownloadClient Client { get; }

    public long SonarrId { get; }

    public long ClientId { get; }

    public long WorkflowId { get; }

    public string Name { get; }

    /// <summary>Sonarr's own name for the connection, which the editor calls it by.</summary>
    public string SonarrName { get; private set; } = string.Empty;

    /// <summary>
    /// Starts a Sonarr and a qBittorrent that saves finished downloads to <paramref name="completedFolder"/>, connects both to Weir, and
    /// adds a TV workflow named <paramref name="name"/> with the given folders, linked to the Sonarr.
    /// </summary>
    public static async Task<SonarrFedWorkflow> StartAsync(string baseUrl, string name, string completedFolder, string watchedFolder, string outputFolder)
    {
        var admin = await AdminSession.OpenAsync(baseUrl);
        var client = FakeDownloadClient.Start("qbittorrent", QBittorrentUser, QBittorrentSecret, completedFolder);
        var sonarr = FakeManager.StartArr("sonarr");
        var endpoint = new Uri(client.BaseUrl);
        sonarr.Route("GET", "/api/v3/downloadclient", Json(ClientsOf(endpoint)));
        sonarr.Route("GET", "/api/v3/config/downloadclient", Json("""{"downloadClientWorkingFolders":"_UNPACK_|_FAILED_","enableCompletedDownloadHandling":true,"autoRedownloadFailed":true,"id":1}"""));
        sonarr.Route("GET", "/api/v3/remotepathmapping", new JsonArray());
        var made = new List<IDisposable> { admin, client, sonarr };
        try
        {
            var clientConnection = await admin.PostWithCsrfAsync($"{WeirClient.Api}/download-clients/connections", new JsonObject
            {
                ["kind"] = "qbittorrent",
                ["base_url"] = client.BaseUrl,
                ["username"] = QBittorrentUser,
                ["password"] = QBittorrentSecret,
                ["enabled"] = true,
            });
            Assert.True(clientConnection.Status == HttpStatusCode.Created, clientConnection.ToString());
            var connection = await admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections", new JsonObject
            {
                ["kind"] = "sonarr",
                ["base_url"] = sonarr.BaseUrl,
                ["api_key"] = sonarr.ApiKey,
                ["enabled"] = true,
            });
            Assert.True(connection.Status == HttpStatusCode.Created, connection.ToString());
            var workflow = await admin.PostWithCsrfAsync(Libraries, new JsonObject
            {
                ["name"] = name,
                ["media_type"] = "tv",
                ["watched_folder"] = watchedFolder,
                ["output_folder"] = outputFolder,
                ["manager_connection_ids"] = new JsonArray((long)connection.Fields["id"]!),
            });
            Assert.True(workflow.Status == HttpStatusCode.Created, workflow.ToString());
            return new SonarrFedWorkflow(admin, sonarr, client, (long)connection.Fields["id"]!, (long)clientConnection.Fields["id"]!, (long)workflow.Fields["id"]!, name)
            {
                SonarrName = (string)connection.Fields["name"]!,
            };
        }
        catch
        {
            foreach (var disposable in made)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Admin.DeleteWithCsrfBodyAsync($"{Libraries}/{WorkflowId}");
            await ManagerConnections.RemoveAsync(Admin, SonarrId);
            await Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/download-clients/connections/{ClientId}");
        }
        finally
        {
            Admin.Dispose();
            Sonarr.Dispose();
            Client.Dispose();
        }
    }

    private static Reply Json(string text) => Reply.Ok(JsonNode.Parse(text));

    private static string ClientsOf(Uri endpoint) => $$"""
        [{"enable":true,"protocol":"torrent","priority":1,"removeCompletedDownloads":true,"removeFailedDownloads":true,"name":"qBittorrent",
          "fields":[{"order":0,"name":"host","label":"Host","value":"{{endpoint.Host}}","type":"textbox","advanced":false,"privacy":"normal","isFloat":false},
                    {"order":1,"name":"port","label":"Port","value":{{endpoint.Port}},"type":"textbox","advanced":false,"privacy":"normal","isFloat":false},
                    {"order":6,"name":"tvCategory","label":"Category","value":"tv-sonarr","type":"textbox","advanced":false,"privacy":"normal","isFloat":false}],
          "implementationName":"qBittorrent","implementation":"QBittorrent","configContract":"QBittorrentSettings","tags":[],"id":1}]
        """;
}
