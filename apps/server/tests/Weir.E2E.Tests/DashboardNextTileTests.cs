using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Next tile lists what Weir does next on its own, soonest first, from the times the server gives: each workflow's next look at its
/// watched folder. The list is read back from the API here, so the tile is held to the server's own times.
/// </summary>
public sealed class DashboardNextTileTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";

    private sealed record Due(string Label, DateTimeOffset At);

    private static async Task<IReadOnlyList<Due>> DueFromServerAsync(ProcessingRig rig)
    {
        var listed = await rig.Admin.GetAsync(Libraries);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        var due = new List<Due>();
        foreach (var workflow in listed.Elements.Select(row => row!.AsObject()))
        {
            if ((bool)workflow["enabled"]! && !string.IsNullOrWhiteSpace((string?)workflow["watched_folder"]) && Time(workflow["next_look_at"]) is { } look)
            {
                due.Add(new Due($"Scan {(string)workflow["name"]!}", look));
            }
        }

        return [.. due.OrderBy(item => item.At)];
    }

    private static DateTimeOffset? Time(JsonNode? text) =>
        text is JsonValue value && value.TryGetValue<string>(out var raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [E2EFact]
    public async Task The_list_is_the_servers_own_scan_times_soonest_first_and_a_paused_Weir_says_nothing_new_starts()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            await rig.AddWorkflowAsync("E2E Shorts");
            IReadOnlyList<Due> due = [];
            await Poll.UntilAsync(async () => (due = await DueFromServerAsync(rig)).Count == 2, "Weir to plan its first look at the new workflow's folder", TimeSpan.FromSeconds(60));
            var page = await NewPageAsync(new ViewportSize { Width = 1600, Height = 1000 });
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            var next = page.GetByRole(AriaRole.Region, new() { Name = "Next", Exact = true });
            var then = page.GetByTestId("live-next-then").GetByRole(AriaRole.Listitem);

            await Expect(next).ToContainTextAsync(due[0].Label, new() { Timeout = PushMs });
            await Expect(then.First).ToContainTextAsync($"then {due[1].Label}", new() { Timeout = PushMs });
            Assert.StartsWith($"then {due[1].Label}", (await then.AllInnerTextsAsync()).Single(), StringComparison.Ordinal);

            await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: true);

            await Expect(next).ToContainTextAsync("Running files finish first.", new() { Timeout = PushMs });
            await Expect(next).Not.ToContainTextAsync(due[0].Label);

            await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: false);

            await Expect(next).ToContainTextAsync(due[0].Label, new() { Timeout = PushMs });
        });
    }

    [E2EFact]
    public async Task A_workflow_that_is_switched_off_or_has_no_watched_folder_is_left_out_not_guessed()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync(new ViewportSize { Width = 1600, Height = 1000 });
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            var next = page.GetByRole(AriaRole.Region, new() { Name = "Next", Exact = true });
            var due = await DueFromServerAsync(rig);

            // Only Movies has a folder to look at; TV, with none set, is not listed.
            Assert.Equal(["Scan Movies"], due.Select(item => item.Label));
            await Expect(next).ToContainTextAsync("Scan Movies", new() { Timeout = PushMs });
            await Expect(next).Not.ToContainTextAsync("Scan TV");
            await Expect(page.GetByTestId("live-next-then").GetByRole(AriaRole.Listitem)).ToHaveCountAsync(0);
        });
    }
}
