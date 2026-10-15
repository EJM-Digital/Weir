using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The screens follow Weir live: they say when the live connection drops, catch up when it is back, and need no reload.</summary>
public sealed class LiveConnectionTests(E2EServer server) : E2ETestBase(server)
{
    // The longest the page may take to notice: the 5 s the stream asks the browser to wait before it tries again.
    private const float ReconnectMs = 20_000;

    /// <summary>Signs in and waits until the page has its live stream open.</summary>
    private async Task<IPage> OpenLiveShellAsync()
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        return page;
    }

    /// <summary>Records, inside the page, each state of the live-connection banner and the pause badge as they change, so a state that lasted a moment is not missed between two looks.</summary>
    private static async Task RecordBannerAndBadgeAsync(IPage page) =>
        await page.EvaluateAsync(
            """
            () => {
              const states = (window.__weirShown = []);
              const look = () => {
                const state = {
                  banner: document.querySelector('[data-testid="live-connection-banner"]') !== null,
                  badge: document.querySelector('[data-testid="pause-badge"]') !== null,
                };
                const last = states.at(-1);
                if (!last || last.banner !== state.banner || last.badge !== state.badge) states.push(state);
              };
              new MutationObserver(look).observe(document.body, { subtree: true, childList: true, attributes: true });
              look();
            }
            """);

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    [E2EFact]
    public async Task The_banner_follows_the_connection_and_the_page_catches_up_without_a_reload()
    {
        var page = await OpenLiveShellAsync();
        var banner = page.GetByTestId("live-connection-banner");
        await Expect(banner).ToHaveCountAsync(0);
        await RecordBannerAndBadgeAsync(page);

        await page.Context.SetOfflineAsync(true);

        await Expect(banner).ToHaveTextAsync("Live updates paused: can't reach Weir. Reconnecting…", new() { Timeout = ReconnectMs });
        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);
        Assert.True(await WeirApi.IsPausedAsync(page.Context, BaseUrl), "The server did not take the pause.");
        await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0);

        await page.Context.SetOfflineAsync(false);

        await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
        await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync(new() { Timeout = ReconnectMs });
        await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();

        // The pause was made while the page could not hear of it, so the badge was drawn only after the banner had gone: at no time
        // did the page show both, which a push or a fetch that got through while offline would have caused.
        var shown = await page.EvaluateAsync<System.Text.Json.JsonElement>("() => window.__weirShown");
        Assert.DoesNotContain(shown.EnumerateArray(), state => state.GetProperty("banner").GetBoolean() && state.GetProperty("badge").GetBoolean());
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task Pausing_and_resuming_through_the_api_changes_the_header_without_a_reload()
    {
        var page = await OpenLiveShellAsync();

        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);
        await Expect(page.GetByTestId("pause-badge")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();

        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);
        await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();

        await AssertNotReloadedAsync(page);
    }
}
