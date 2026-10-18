using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Library page while its live link to Weir is down, in a real browser: the page says it cannot reach Weir, and when the link is
/// back it lists what changed while it was away, with no reload.
/// </summary>
public sealed class LibraryLiveLinkTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;

    // The longest the page may take to notice: the 5 s the stream asks the browser to wait before it tries again.
    private const float ReconnectMs = 20_000;

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";

    [E2EFact]
    public async Task A_film_added_and_scanned_while_the_link_is_down_is_listed_once_the_link_is_back_without_a_reload()
    {
        await using var rig = await LibraryRig.StartAsync();
        await rig.AddFilmAsync(Arrival, ["eng"], []);
        await rig.ScanAsync();
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        var rows = page.GetByTestId("library-row");
        await Expect(rows).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var banner = page.GetByTestId("live-connection-banner");
        await Expect(banner).ToHaveCountAsync(0);

        await page.Context.SetOfflineAsync(true);

        await Expect(banner).ToHaveTextAsync("Live updates paused: can't reach Weir. Reconnecting…", new() { Timeout = ReconnectMs });
        await rig.AddFilmAsync(Heat, ["eng"], []);
        await rig.ScanAsync();
        await Expect(rows).ToHaveCountAsync(1);

        await page.Context.SetOfflineAsync(false);

        await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
        await Expect(rows).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-scan")).ToContainTextAsync("checked", new() { Timeout = ServerMs });
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }
}
