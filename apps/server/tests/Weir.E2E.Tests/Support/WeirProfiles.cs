using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>Sets up and clears profiles (the rule sets of Rules › Profiles) through Weir's API, so the server makes each change as it would for anyone else.</summary>
public static class WeirProfiles
{
    private const string RuleSets = $"{WeirClient.Api}/processing/rule-sets";
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";

    /// <summary>A new profile with Weir's default rules; returns its id.</summary>
    public static async Task<long> CreateAsync(WeirClient client, string name)
    {
        var created = await client.PostWithCsrfAsync(RuleSets, new JsonObject { ["name"] = name });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    /// <summary>Makes the workflow use the profile, as choosing it in the workflow's editor does.</summary>
    public static async Task UseInWorkflowAsync(WeirClient client, long workflowId, long profileId)
    {
        var read = await client.GetAsync($"{Libraries}/{workflowId}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        var body = LibraryBodies.Unchanged(read.Fields);
        body["rule_set_id"] = profileId;
        var saved = await client.PutWithCsrfAsync($"{Libraries}/{workflowId}", body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    /// <summary>The ids of every profile, in the order Weir lists them.</summary>
    public static async Task<IReadOnlyList<long>> IdsAsync(WeirClient client)
    {
        var listed = await client.GetAsync(RuleSets);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return [.. listed.Elements.Select(row => (long)row!["id"]!)];
    }

    /// <summary>Stops the workflow using any profile and removes the profile, whether or not a test already did.</summary>
    public static async Task ClearAsync(WeirClient client, long profileId, params long[] workflowIds)
    {
        foreach (var workflowId in workflowIds)
        {
            var read = await client.GetAsync($"{Libraries}/{workflowId}");
            if (read.Status == HttpStatusCode.OK && (long?)read.Fields["rule_set_id"] == profileId)
            {
                var body = LibraryBodies.Unchanged(read.Fields);
                body["rule_set_id"] = null;
                var saved = await client.PutWithCsrfAsync($"{Libraries}/{workflowId}", body);
                Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
            }
        }

        if ((await IdsAsync(client)).Contains(profileId))
        {
            var removed = await client.DeleteWithCsrfBodyAsync($"{RuleSets}/{profileId}");
            Assert.True(removed.Status == HttpStatusCode.NoContent, removed.ToString());
        }
    }
}
