using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The Dashboard's Today figure follows a pass to its end with no reload.</summary>
public sealed class LiveTodayFigureTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    // A pass finishing is the server's work, not a push, and the totals follow the stream at a pace of their own; a ceiling, not a wait.
    private const float FinishMs = 60_000;
    private const string FileName = "Harbour Lights 2024.mkv";

    [E2EFact]
    public async Task Finishing_a_pass_raises_the_Today_figure_and_empties_Working_on_now_without_a_reload()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var today = page.GetByTestId("live-done-today");
        var working = page.GetByTestId("live-working-count");
        await Expect(today).ToHaveTextAsync("0");
        await rig.HandOffAsync("h-today", FileName);
        await Expect(working).ToHaveTextAsync("1", new() { Timeout = PushMs });
        await Expect(today).ToHaveTextAsync("0");

        rig.ReleasePass(FileName);

        await Expect(today).ToHaveTextAsync("1", new() { Timeout = FinishMs });
        await Expect(working).ToHaveTextAsync("0", new() { Timeout = FinishMs });
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }
}
