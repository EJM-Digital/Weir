using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The Dashboard's Today figure follows a pass to its end with no reload, and the file lands on Just finished, where its tile opens its story.</summary>
public sealed class LiveTodayFigureTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    // A pass finishing is the server's work, not a push, and the totals follow the stream at a pace of their own.
    private const float FinishMs = 30_000;
    private const string FileName = "Harbour Lights 2024.mkv";

    [E2EFact]
    public async Task Finishing_a_pass_raises_the_Today_figure_and_empties_Working_on_now_and_its_Just_finished_tile_opens_its_story()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
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

            var tile = page.GetByRole(AriaRole.Region, new() { Name = "Just finished" })
                .GetByRole(AriaRole.Button, new() { NameRegex = new Regex("harbour lights", RegexOptions.IgnoreCase) });
            await tile.ClickAsync();

            await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Harbour Lights");
        });
    }
}
