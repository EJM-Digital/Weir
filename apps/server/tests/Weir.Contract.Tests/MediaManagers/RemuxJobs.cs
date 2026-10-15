using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>The persisted Processing jobs a hand-off queues, read through the job inspection API.</summary>
internal static class RemuxJobs
{
    public const string Kind = "processing.file.remux_pass.v1";
    public const string CallbackPath = "/api/integrations/processors/events";

    private const int InspectionLimit = 100;

    /// <summary>Every persisted remux job row (workers are off unless a test turns them on, so intake's rows stay where they were put).</summary>
    public static async Task<List<JsonObject>> ListAsync(WeirClient client)
    {
        var inspected = await client.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", InspectionLimit));
        Assert.True(inspected.Status == HttpStatusCode.OK, inspected.ToString());
        return [.. inspected.Fields["jobs"]!.AsArray().Select(job => job!.AsObject()).Where(job => (string?)job["job_kind"] == Kind)];
    }

    public static JsonObject Payload(JsonObject job) =>
        JsonNode.Parse((string?)job["payload_json"] is { Length: > 0 } text ? text : "{}")!.AsObject();

    public static string HandoffDedupeKey(string handoffId, string sourceKey = "deluno") => $"{Kind}:{sourceKey}:handoff:{handoffId}";

    /// <summary>The remux jobs of one hand-off.</summary>
    public static async Task<List<JsonObject>> ForHandoffAsync(WeirClient client, string handoffId, string sourceKey = "deluno")
    {
        var key = HandoffDedupeKey(handoffId, sourceKey);
        return [.. (await ListAsync(client)).Where(job => (string?)job["dedupe_key"] == key)];
    }

    /// <summary>The status of the newest remux job for a hand-off, matched on its payload because cancelling renames the dedupe key.</summary>
    public static async Task<string?> StatusForHandoffAsync(WeirClient client, string handoffId) =>
        await StatusesForHandoffAsync(client, handoffId) is [var newest, ..] ? newest : null;

    /// <summary>The statuses of every remux job for a hand-off, newest first, matched on the payload as <see cref="StatusForHandoffAsync"/> is.</summary>
    public static async Task<IReadOnlyList<string>> StatusesForHandoffAsync(WeirClient client, string handoffId) =>
        [.. (await ListAsync(client))
            .Where(job => (string?)Payload(job)["origin"]?["handoff_id"] == handoffId)
            .Select(job => (string)job["status"]!)];
}
