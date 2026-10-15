using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Weir keeps no link between a workflow and a download client: a client is part of a workflow's folder chain because it saves into
/// the workflow's watched folder, or because the media manager the workflow is linked to uses it. Removing the connection must
/// therefore leave the workflow, its link to the manager and every read of them working, with the client simply no longer listed.
/// </summary>
[ContractArea("media_managers")]
public sealed class DownloadClientRemovalTests(DownloadClientServerFixture fixture) : IClassFixture<DownloadClientServerFixture>
{
    private const string Secret = "stored-secret-9a3b";

    private static readonly DownloadClientKind Sabnzbd = DownloadClients.Kind("sabnzbd");

    private static string ManagerClientsJson(FakeDownloadClient client)
    {
        var endpoint = new Uri(client.BaseUrl);
        return $$"""[{"enable":true,"protocol":"usenet","name":"SABnzbd","implementation":"Sabnzbd","fields":[{"name":"host","value":"{{endpoint.Host}}"},{"name":"port","value":{{endpoint.Port}}}]}]""";
    }

    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await fixture.Server.CreateAdminClientAsync();
        await DownloadClients.ClearAsync(admin);
        await RemoveManagersAsync(admin);
        return admin;
    }

    /// <summary>A manager a workflow still comes from is not removed, so the workflows are let go of first.</summary>
    private static async Task RemoveManagersAsync(WeirClient admin)
    {
        foreach (var row in (await admin.GetAsync(ManagerConnections.Route)).Elements)
        {
            var id = JsonFields.Id(row!);
            var unlinked = await admin.PostWithCsrfAsync($"{ManagerConnections.Route}/{id}/unlink-workflows");
            Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());
            Assert.Equal(HttpStatusCode.NoContent, (await ManagerConnections.DeleteAsync(admin, id)).Status);
        }
    }

    private static async Task<JsonObject> FolderChainAsync(WeirClient admin, JsonObject library)
    {
        var chain = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{JsonFields.Id(library)}/folder-chain");
        Assert.True(chain.Status == HttpStatusCode.OK, chain.ToString());
        return chain.Fields;
    }

    /// <summary>What the person set on a workflow, without the counts and times that move on their own while it is scanned.</summary>
    private static string Settings(JsonObject workflow) => new JsonObject
    {
        ["name"] = workflow["name"]?.DeepClone(),
        ["media_type"] = workflow["media_type"]?.DeepClone(),
        ["enabled"] = workflow["enabled"]?.DeepClone(),
        ["watched_folder"] = workflow["watched_folder"]?.DeepClone(),
        ["output_folder"] = workflow["output_folder"]?.DeepClone(),
        ["manager_connection_ids"] = workflow["manager_connection_ids"]?.DeepClone(),
    }.ToJsonString();

    private static async Task<JsonObject> WorkflowAsync(WeirClient admin, JsonObject library)
    {
        var read = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{JsonFields.Id(library)}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        return read.Fields;
    }

    private static async Task AssertConnectionGoneAsync(WeirClient admin, JsonObject connection)
    {
        var route = $"{DownloadClients.Route}/{JsonFields.Id(connection)}";
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(route)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostWithCsrfAsync($"{route}/test")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadClients.DeleteAsync(admin, connection)).Status);
        Assert.Empty((await admin.GetAsync(DownloadClients.Route)).Elements);
        var suggestions = await admin.GetAsync($"{WeirClient.Api}/download-clients/suggestions", ("media_type", "movie"));
        Assert.True(suggestions.Status == HttpStatusCode.OK, suggestions.ToString());
        Assert.Empty(suggestions.Elements);
    }

    [Fact]
    public async Task A_client_that_saves_into_a_workflows_watched_folder_drops_out_of_its_chain_when_removed()
    {
        using var admin = await OperatorAsync();
        var folders = fixture.Library("saves-into-watched");
        using var client = DownloadClients.StartFake(Sabnzbd, Secret, completedFolder: folders.Watched);
        var connection = await DownloadClients.CreateAsync(admin, Sabnzbd, client, Secret);
        var workflow = await ProcessingLibraries.CreateAsync(admin, "Fed by SABnzbd", "movie", folders);
        var before = await FolderChainAsync(admin, workflow);
        var listed = Assert.Single(before["download_clients"]!.AsArray())!;
        Assert.Equal(JsonFields.Id(connection), (int)listed["connection_id"]!);
        Assert.True((bool)listed["ready"]!, before.ToJsonString());

        var removed = await DownloadClients.DeleteAsync(admin, connection);

        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        var after = await FolderChainAsync(admin, workflow);
        Assert.Empty(after["download_clients"]!.AsArray());
        Assert.Equal(before["local"]!.ToJsonString(), after["local"]!.ToJsonString());
        Assert.Equal(before["managers"]!.ToJsonString(), after["managers"]!.ToJsonString());
        Assert.Equal(Settings(workflow), Settings(await WorkflowAsync(admin, workflow)));
        Assert.Contains(await ProcessingLibraries.ListAsync(admin), row => JsonFields.Id(row!) == JsonFields.Id(workflow));
        await AssertConnectionGoneAsync(admin, connection);
    }

    [Fact]
    public async Task A_client_the_linked_manager_uses_drops_out_of_the_chain_when_removed_and_the_manager_link_stays()
    {
        using var admin = await OperatorAsync();
        var folders = fixture.Library("manager-uses-client");
        using var client = DownloadClients.StartFake(Sabnzbd, Secret, completedFolder: folders.Output);
        using var radarr = FakeManager.StartArr("radarr", rootFolders: [folders.Output]);
        radarr.Route("GET", "/api/v3/downloadclient", Reply.Ok(JsonNode.Parse(ManagerClientsJson(client))));
        radarr.Route("GET", "/api/v3/config/downloadclient", new JsonObject { ["enableCompletedDownloadHandling"] = true, ["id"] = 1 });
        radarr.Route("GET", "/api/v3/remotepathmapping", new JsonArray());
        var manager = await ManagerConnections.CreateAsync(
            admin, new JsonObject { ["kind"] = "radarr", ["base_url"] = radarr.BaseUrl, ["api_key"] = radarr.ApiKey });
        Assert.True(manager.Status == HttpStatusCode.Created, manager.ToString());
        var connection = await DownloadClients.CreateAsync(admin, Sabnzbd, client, Secret);
        var workflow = await ProcessingLibraries.CreateAsync(
            admin, "Fed by Radarr", "movie", folders, new JsonObject { ["manager_connection_ids"] = new JsonArray(JsonFields.Id(manager.Fields)) });
        var before = await FolderChainAsync(admin, workflow);
        Assert.Equal(JsonFields.Id(connection), (int)Assert.Single(before["download_clients"]!.AsArray())!["connection_id"]!);

        var removed = await DownloadClients.DeleteAsync(admin, connection);

        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        var after = await FolderChainAsync(admin, workflow);
        Assert.Empty(after["download_clients"]!.AsArray());
        Assert.Equal(before["local"]!.ToJsonString(), after["local"]!.ToJsonString());
        var kept = await WorkflowAsync(admin, workflow);
        Assert.Equal([JsonFields.Id(manager.Fields)], kept["manager_connection_ids"]!.AsArray().Select(id => (int)id!));
        Assert.Equal(Settings(workflow), Settings(kept));
        await AssertConnectionGoneAsync(admin, connection);
    }
}
