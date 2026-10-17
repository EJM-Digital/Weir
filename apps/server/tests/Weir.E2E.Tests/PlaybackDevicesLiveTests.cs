using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The devices chosen for the Direct Play badge follow Weir live: a choice saved in one browser shows in another with no reload.</summary>
public sealed class PlaybackDevicesLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const string DeviceName = "Apple TV 4K";

    /// <summary>Signs in, opens Rules › Playback devices and waits until the page has its live stream open.</summary>
    private async Task<IPage> OpenPlaybackDevicesAsync()
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Navigation.OpenTabAsync(page, "Rules", "Playback devices");
        await Expect(page.GetByTestId("processing-direct-play-section")).ToBeVisibleAsync();
        return page;
    }

    [E2EFact]
    public async Task A_device_chosen_in_one_browser_is_ticked_in_another_without_a_reload()
    {
        var watching = await OpenPlaybackDevicesAsync();
        await watching.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var choosing = await OpenPlaybackDevicesAsync();
        var watchedBox = watching.GetByRole(AriaRole.Checkbox, new() { Name = DeviceName, Exact = true });
        await Expect(watchedBox).Not.ToBeCheckedAsync();

        await choosing.GetByRole(AriaRole.Checkbox, new() { Name = DeviceName, Exact = true }).CheckAsync();
        await choosing.GetByRole(AriaRole.Button, new() { Name = "Save devices", Exact = true }).ClickAsync();

        await Expect(watchedBox).ToBeCheckedAsync();
        Assert.True(await watching.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }
}
