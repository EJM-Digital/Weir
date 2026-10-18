using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>What Weir holds from the first-run wizard, read back through its API as the signed-in person in a browser context.</summary>
public static class SetupState
{
    private const string Api = WeirClient.Api;

    /// <summary>The app settings the wizard saves: the time zone, the backup schedule and <c>setup_wizard_state</c>.</summary>
    public static async Task<JsonObject> SettingsAsync(IBrowserContext context, string baseUrl) =>
        (await ReadAsync(context, $"{baseUrl}{Api}/suite/settings")).AsObject();

    /// <summary>The workflows, the first of each media type being the ones the wizard fills in.</summary>
    public static async Task<IReadOnlyList<JsonObject>> WorkflowsAsync(IBrowserContext context, string baseUrl) =>
        [.. (await ReadAsync(context, $"{baseUrl}{Api}/processing/libraries")).AsArray().Select(row => row!.AsObject())];

    /// <summary>The first workflow of a media type ("movie" or "tv"), in the order Weir shows them.</summary>
    public static async Task<JsonObject> FirstWorkflowAsync(IBrowserContext context, string baseUrl, string mediaType) =>
        (await WorkflowsAsync(context, baseUrl))
            .Where(row => (string)row["media_type"]! == mediaType)
            .OrderBy(row => (int)row["display_order"]!)
            .First();

    /// <summary>Marks the wizard skipped through the API, as the Skip button does, so a sign-in lands on the Dashboard.</summary>
    public static async Task SkipWizardAsync(WeirClient client)
    {
        var current = await client.GetAsync($"{Api}/suite/settings");
        Assert.True(current.Status == HttpStatusCode.OK, current.ToString());
        var saved = await client.PutWithCsrfAsync($"{Api}/suite/settings", new JsonObject
        {
            ["setup_wizard_state"] = "skipped",
            ["app_timezone"] = current.Fields["app_timezone"]?.DeepClone(),
            ["log_retention_days"] = (int)current.Fields["log_retention_days"]!,
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    private static async Task<JsonNode> ReadAsync(IBrowserContext context, string url)
    {
        await using var response = await context.APIRequest.GetAsync(url);
        Assert.True(response.Ok, $"Reading {url} answered {response.Status}.");
        return JsonNode.Parse(await response.TextAsync())!;
    }
}
