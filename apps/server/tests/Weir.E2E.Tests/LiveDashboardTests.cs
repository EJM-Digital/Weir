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

    // How much of the page's own time passes in the quiet test: longer than any read-out was ever read again by a timer, and still
    // inside the 40 s after which the page treats a silent stream as dead.
    private const long QuietMs = 30_000;

    private static readonly string[] ReadOutPaths =
    [
        "/api/v1/pause",
        "/api/v1/processing/files",
        "/api/v1/processing/files-at-once",
        "/api/v1/processing/maintenance",
        "/api/v1/processing/libraries",
        "/api/v1/processing/jobs/inspection",
    ];

    // Counts the page's reads that have not been answered yet and lists those that have, so a test knows when the page has read all it
    // means to on opening. The live stream is not a read: it stays open for as long as the page does.
    private const string CountReadsInFlight = """
        (() => {
          const original = window.fetch.bind(window);
          window.__readsInFlight = 0;
          window.__readsDone = [];
          window.fetch = (input, init) => {
            const address = new URL(typeof input === 'string' ? input : input.url, window.location.href);
            const counted = address.pathname !== '/api/v1/activity/stream';
            if (counted) window.__readsInFlight += 1;
            return original(input, init).finally(() => {
              if (!counted) return;
              window.__readsInFlight -= 1;
              window.__readsDone.push(address.pathname);
            });
          };
        })();
        """;

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

    // The server's expiry task looks every 30 s, and is run here ten times as often; this is the most the page is given to hear of it.
    private const float TimedPauseMs = 20_000;

    private const int QuickTasks = 10;

    [E2EFact]
    public async Task A_timed_pause_running_out_flips_the_header_back_to_running_by_itself()
    {
        await using var own = await E2EServer.StartWithQuickTasksAsync(QuickTasks);
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, baseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await MarkPageAsync(page);

        await WeirApi.SetPausedForAsync(page.Context, baseUrl, minutes: 1);
        await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync(new() { Timeout = PushMs });
        Assert.True(await WeirApi.IsPausedAsync(page.Context, baseUrl));

        // The minute passes: nothing but the server's own sweep lifts the pause, and the page is told when it does.
        E2EDatabase.RunTimedPauseOut(own.DatabasePath);

        await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0, new() { Timeout = TimedPauseMs });
        Assert.False(await WeirApi.IsPausedAsync(page.Context, baseUrl));
        await AssertNotReloadedAsync(page);
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

        // The page's own clock, which a test can run on: every timer the page has fires when it is due, without anyone waiting.
        await page.Clock.InstallAsync();
        await page.AddInitScriptAsync(CountReadsInFlight);
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("0");
        await page.WaitForFunctionAsync(
            "(paths) => window.__readsInFlight === 0 && paths.every((path) => window.__readsDone.includes(path))",
            ReadOutPaths,
            new() { Timeout = PushMs });
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

        await page.Clock.RunForAsync(QuietMs);

        lock (asked)
        {
            Assert.Empty(asked);
        }

        // The recording sees what the page asks for: a change the server pushes is read at once.
        await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: true);
        await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync(new() { Timeout = PushMs });
        lock (asked)
        {
            Assert.Contains("/api/v1/pause", asked);
        }
    }
}
