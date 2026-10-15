using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// What Weir holds that a remove dialog could take away: its workflows, profiles and media manager connections, one line each, as
/// the server reports them. Two snapshots with the same lines mean nothing was removed, unlinked, switched off or changed.
/// </summary>
public static class SetupSnapshot
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string RuleSets = $"{WeirClient.Api}/processing/rule-sets";
    private const string Connections = $"{WeirClient.Api}/media-managers/connections";

    public static async Task<IReadOnlyList<string>> TakeAsync(WeirClient client)
    {
        var workflows = await ReadAsync(client, Libraries);
        var profiles = await ReadAsync(client, RuleSets);
        var connections = await ReadAsync(client, Connections);
        return
        [
            .. workflows.Select(row => $"workflow {Field(row, "id")} {Field(row, "name")} on={Field(row, "enabled")} profile={Field(row, "rule_set_id")} managers={Field(row, "manager_connection_ids")}"),
            .. profiles.Select(row => $"profile {Field(row, "id")} {Field(row, "name")} used_by={Field(row, "used_by_library_count")}"),
            .. connections.Select(row => $"connection {Field(row, "id")} {Field(row, "name")} on={Field(row, "enabled")}"),
        ];
    }

    private static async Task<IEnumerable<JsonObject>> ReadAsync(WeirClient client, string path)
    {
        var listed = await client.GetAsync(path);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return listed.Elements.Select(row => row!.AsObject()).OrderBy(row => (long)row["id"]!);
    }

    private static string Field(JsonObject row, string name) => row[name]?.ToJsonString() ?? "none";
}
