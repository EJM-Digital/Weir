using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Every screen of the signed-in app, and the menus and panels the shell opens, run through the accessibility check at a desktop and
/// a phone width in the light and the dark theme. On a phone the page must also fit the width, with no sideways scroll. Each place is
/// checked and every problem is reported together, so one run names everything that needs fixing.
/// </summary>
public sealed class AccessibilitySweepTests(E2EServer server) : E2ETestBase(server)
{
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 720 };
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 844 };

    private sealed record Place(string Name, string Address, string Ready);

    // Every address the app answers, with the part of the screen that shows once it has drawn.
    private static readonly Place[] Places =
    [
        new("Dashboard", "/", "processing-page"),
        new("Activity", "/activity", "activity-page"),
        new("Library", "/library", "library-page"),
        new("Workflows > File paths", "/setup/workflows", "processing-libraries-section"),
        new("Workflows > Schedule", "/setup/workflows/schedule", "processing-schedules-section"),
        new("Connections > Media managers", "/setup/connections", "suite-settings-media-managers"),
        new("Connections > Download clients", "/setup/connections/download-clients", "suite-settings-download-clients-tab"),
        new("Connections > Alerts", "/setup/connections/alerts", "suite-settings-notifications"),
        new("Rules > Profiles", "/setup/rules", "processing-rule-set-workspace"),
        new("Rules > Playback devices", "/setup/rules/devices", "processing-direct-play-section"),
        new("Performance > Speed", "/setup/performance", "processing-process-settings"),
        new("Performance > Cleanup", "/setup/performance/cleanup", "processing-maintenance-section"),
        new("Performance > Timers", "/setup/performance/timers", "processing-timers-section"),
        new("System > About", "/system", "suite-settings-global"),
        new("System > Backups", "/system?tab=backups", "suite-settings-backup-tab"),
        new("System > Security", "/system?tab=security", "suite-settings-security"),
        new("System > Logs", "/system?tab=logs", "log-feed"),
        new("Not found", "/nothing-lives-here", "shell-ready"),
    ];

    [E2EFact]
    public async Task Every_screen_and_shell_menu_passes_the_accessibility_check_on_a_desktop()
    {
        var problems = new List<string>();
        foreach (var scheme in new[] { ColorScheme.Dark, ColorScheme.Light })
        {
            var page = await NewPageAsync(Desktop, colorScheme: scheme);
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            await SweepPlacesAsync(page, $"Desktop, {scheme}", fitsWidth: false, problems);
            await SweepShellMenusAsync(page, $"Desktop, {scheme}", problems);
        }

        Assert.True(problems.Count == 0, ReportOf(problems));
    }

    [E2EFact]
    public async Task Every_screen_and_shell_menu_passes_the_accessibility_check_on_a_phone_and_fits_its_width()
    {
        var problems = new List<string>();
        foreach (var scheme in new[] { ColorScheme.Dark, ColorScheme.Light })
        {
            var page = await NewPageAsync(Phone, colorScheme: scheme);
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            await SweepPlacesAsync(page, $"Phone, {scheme}", fitsWidth: true, problems);
            await OpenNavigationDrawerAsync(page);
            await ScanAsync(page, $"Phone, {scheme}: the side menu", fitsWidth: false, problems);
            await SweepShellMenusAsync(page, $"Phone, {scheme}", problems);
        }

        Assert.True(problems.Count == 0, ReportOf(problems));
    }

    [E2EFact]
    public async Task Every_screen_fits_its_width_from_a_small_phone_to_a_wide_desktop()
    {
        var problems = new List<string>();
        foreach (var width in new[] { 320, 375, 768, 1024, 1440 })
        {
            var page = await NewPageAsync(new ViewportSize { Width = width, Height = 800 });
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            foreach (var place in Places)
            {
                await page.GotoAsync($"{BaseUrl}{place.Address}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Expect(page.GetByTestId(place.Ready)).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
                if (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth"))
                {
                    problems.Add($"{width}px wide, {place.Name}: the page is wider than the screen, so it scrolls sideways");
                }
            }

            await page.CloseAsync();
        }

        Assert.True(problems.Count == 0, ReportOf(problems));
    }

    private static string ReportOf(IEnumerable<string> problems) =>
        $"These places break accessibility rules or their width:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}";

    private async Task SweepPlacesAsync(IPage page, string setting, bool fitsWidth, List<string> problems)
    {
        foreach (var place in Places)
        {
            await page.GotoAsync($"{BaseUrl}{place.Address}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByTestId(place.Ready)).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
            await ScanAsync(page, $"{setting}: {place.Name}", fitsWidth, problems);
        }
    }

    // The header's pause menu, the user menu at the foot of the side menu and the panel the Logs tab opens.
    private async Task SweepShellMenusAsync(IPage page, string setting, List<string> problems)
    {
        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        await page.GetByTestId("pause-open").ClickAsync();
        await Expect(page.GetByTestId("pause-menu")).ToBeVisibleAsync();
        await ScanAsync(page, $"{setting}: the pause menu", fitsWidth: false, problems);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId("pause-menu")).ToHaveCountAsync(0);

        await OpenNavigationDrawerIfHiddenAsync(page);
        await page.GetByTestId("user-menu").ClickAsync();
        await Expect(page.GetByTestId("sign-out")).ToBeVisibleAsync();
        await ScanAsync(page, $"{setting}: the user menu", fitsWidth: false, problems);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId("sign-out")).ToHaveCountAsync(0);

        await page.GotoAsync($"{BaseUrl}/system?tab=logs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("log-feed")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        var panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Log settings" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Log settings", Exact = true }).ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await ScanAsync(page, $"{setting}: the Log settings panel", fitsWidth: false, problems);
        await page.Keyboard.PressAsync("Escape");
        await Expect(panel).ToHaveCountAsync(0);
    }

    private static async Task OpenNavigationDrawerAsync(IPage page)
    {
        await page.GetByTestId("shell-nav-toggle").ClickAsync();
        await Expect(page.Locator("#mm-primary-sidebar")).ToBeVisibleAsync();
    }

    private static async Task OpenNavigationDrawerIfHiddenAsync(IPage page)
    {
        if (await page.GetByTestId("shell-nav-toggle").IsVisibleAsync() && !await page.Locator("#mm-primary-sidebar").IsVisibleAsync())
        {
            await OpenNavigationDrawerAsync(page);
        }
    }

    private static async Task ScanAsync(IPage page, string place, bool fitsWidth, List<string> problems)
    {
        problems.AddRange((await Axe.ViolationsAsync(page)).Select(line => $"{place}: {line}"));
        if (fitsWidth && await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth"))
        {
            problems.Add($"{place}: the page is wider than the screen, so it scrolls sideways");
        }
    }
}
