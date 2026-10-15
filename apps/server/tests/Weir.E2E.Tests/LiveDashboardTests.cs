using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The Dashboard follows Weir as it changes, with no reload: the changes are made through Weir's API, as anyone else would.</summary>
public sealed class LiveDashboardTests(E2EServer server) : E2ETestBase(server)
{
    // Long enough for the stream's own safety net to be no part of it: the page is told the moment the change commits.
    private const float PushMs = 10_000;

    // The longest any of the read-outs below was once read again by a timer, and a little more.
    private const int QuietMs = 16_000;

    private static readonly string[] ReadOutPaths =
    [
        "/api/v1/pause",
        "/api/v1/processing/files",
        "/api/v1/processing/files-at-once",
        "/api/v1/processing/maintenance",
        "/api/v1/processing/libraries",
        "/api/v1/processing/jobs/inspection",
    ];

    private static async Task MarkPageAsync(IPage page) =>
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    [E2EFact]
    public async Task Pausing_and_resuming_through_the_api_changes_the_header_and_the_Dashboard()
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        await MarkPageAsync(page);
        var nextTile = page.GetByText("Running files finish first.", new() { Exact = true });
        await Expect(nextTile).ToHaveCountAsync(0);

        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);

        await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync(new() { Timeout = PushMs });
        await Expect(nextTile).ToBeVisibleAsync(new() { Timeout = PushMs });
        await Expect(page.GetByText("Paused · nothing new starts.", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = PushMs });

        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);

        await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0, new() { Timeout = PushMs });
        await Expect(nextTile).ToHaveCountAsync(0, new() { Timeout = PushMs });
        await AssertNotReloadedAsync(page);
    }

    // The expiry task looks every 30 s, and a pause lasts at least a minute.
    private const float TimedPauseMs = 100_000;

    [E2EFact]
    public async Task A_timed_pause_running_out_flips_the_header_back_to_running_by_itself()
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await MarkPageAsync(page);
        try
        {
            await WeirApi.SetPausedForAsync(page.Context, BaseUrl, minutes: 1);
            await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync(new() { Timeout = PushMs });

            await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0, new() { Timeout = TimedPauseMs });
            Assert.False(await WeirApi.IsPausedAsync(page.Context, BaseUrl));
            await AssertNotReloadedAsync(page);
        }
        finally
        {
            await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);
        }
    }

    [E2EFact]
    public async Task A_file_handed_over_changes_the_Working_lane_and_the_Working_badge_without_a_reload()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await MarkPageAsync(page);
        var count = page.GetByTestId("live-working-count");
        var badge = page.GetByTestId("nav-processing-working");
        await Expect(count).ToHaveTextAsync("0");
        await Expect(badge).ToHaveCountAsync(0);

        await rig.HandOffAsync("h-one", "Harbour Lights 2024.mkv");

        await Expect(count).ToHaveTextAsync("1", new() { Timeout = PushMs });
        await Expect(badge).ToHaveTextAsync("1", new() { Timeout = PushMs });
        await Expect(page.GetByTestId("live-working")).ToContainTextAsync("Harbour Lights", new() { Timeout = PushMs });

        rig.ReleasePass("Harbour Lights 2024.mkv");
        await rig.WaitForWorkToEndAsync();

        await Expect(count).ToHaveTextAsync("0", new() { Timeout = PushMs });
        await Expect(badge).ToHaveCountAsync(0, new() { Timeout = PushMs });
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task The_Dashboard_asks_for_nothing_while_nothing_changes()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("0");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var asked = new List<string>();
        page.Request += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (request.Method == "GET" && ReadOutPaths.Contains(path, StringComparer.Ordinal))
            {
                lock (asked)
                {
                    asked.Add(path);
                }
            }
        };

        await Task.Delay(QuietMs);

        lock (asked)
        {
            Assert.Empty(asked);
        }
    }
}
