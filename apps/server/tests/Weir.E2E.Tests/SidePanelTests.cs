using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The panel that slides over a page, shown by System › Logs › Log settings: how it opens, what is held still behind it, the four ways to close
/// it (the × button, the dimmed area, Escape, and nothing else), where focus goes in and back, and that it fits a phone.
/// </summary>
public sealed class SidePanelTests(E2EServer server) : E2ETestBase(server)
{
    private static ILocator Panel(IPage page) => page.GetByTestId("log-settings-panel");

    private static ILocator Opener(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Log settings", Exact = true });

    private async Task<IPage> OpenLogsAsync(ViewportSize? viewport = null)
    {
        var page = await NewPageAsync(viewport);
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        // The side menu is a drawer on a phone, so the address is the way there.
        await page.GotoAsync($"{BaseUrl}/system?tab=logs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("log-feed")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        return page;
    }

    private static async Task OpenPanelAsync(IPage page)
    {
        await Opener(page).ClickAsync();
        await Expect(Panel(page)).ToBeVisibleAsync();
    }

    private static async Task ExpectClosedAndPageFreeAsync(IPage page)
    {
        await Expect(Panel(page)).ToHaveCountAsync(0);
        await Expect(Opener(page)).ToBeFocusedAsync();
        await Expect(page.Locator("body")).Not.ToHaveClassAsync(new Regex("mm-drawer-open"));
        await Expect(page.Locator("#mm-primary-sidebar")).Not.ToHaveAttributeAsync("inert", new Regex(".*"));
    }

    [E2EFact]
    public async Task It_opens_as_a_modal_dialog_takes_focus_and_holds_the_page_behind_it_still_and_out_of_reach()
    {
        var page = await OpenLogsAsync();

        await OpenPanelAsync(page);

        var panel = Panel(page);
        await Expect(panel).ToHaveAttributeAsync("role", "dialog");
        await Expect(panel).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(panel).ToHaveAccessibleNameAsync("Log settings");
        await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Log settings", Exact = true })).ToBeVisibleAsync();
        await Expect(panel.GetByText("System · Logs", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(panel).ToBeFocusedAsync();
        // The page keeps its place under the panel: it does not scroll, and the side menu and the page cannot be reached.
        await Expect(page.Locator("body")).ToHaveClassAsync(new Regex("mm-drawer-open"));
        await Expect(page.Locator("body")).ToHaveCSSAsync("overflow", "hidden");
        await Expect(page.Locator("#mm-primary-sidebar")).ToHaveAttributeAsync("inert", "");
        await Axe.AssertNoViolationsAsync(page, "The Log settings panel");
    }

    [E2EFact]
    public async Task The_close_button_the_dimmed_area_and_Escape_each_close_it_and_hand_focus_back()
    {
        var page = await OpenLogsAsync();
        var panel = Panel(page);

        await OpenPanelAsync(page);
        await panel.Locator(".mm-drawer__head").GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await ExpectClosedAndPageFreeAsync(page);

        await OpenPanelAsync(page);
        await page.Locator(".mm-drawer-backdrop").ClickAsync(new() { Position = new() { X = 20, Y = 20 } });
        await ExpectClosedAndPageFreeAsync(page);

        await OpenPanelAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await ExpectClosedAndPageFreeAsync(page);
    }

    [E2EFact]
    public async Task Tab_and_Shift_Tab_stay_inside_the_panel()
    {
        var page = await OpenLogsAsync();
        await OpenPanelAsync(page);
        var panel = Panel(page);
        var inside = await panel.Locator("a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary").CountAsync();
        Assert.True(inside > 1, "The panel has controls to Tab through.");

        for (var press = 0; press <= inside + 1; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await panel.EvaluateAsync<bool>("panel => panel.contains(document.activeElement)"), $"Tab {press + 1} left the panel.");
        }

        for (var press = 0; press <= inside + 1; press++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.True(await panel.EvaluateAsync<bool>("panel => panel.contains(document.activeElement)"), $"Shift+Tab {press + 1} left the panel.");
        }
    }

    [E2EFact]
    public async Task On_a_phone_it_fills_the_width_and_closes_the_same_ways()
    {
        var page = await OpenLogsAsync(new ViewportSize { Width = 390, Height = 844 });

        await OpenPanelAsync(page);

        // The width the page has: a classic scrollbar, where there is one, is not part of it.
        var width = await page.EvaluateAsync<int>("() => document.documentElement.clientWidth");
        var box = (await Panel(page).BoundingBoxAsync())!;
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"The panel runs from {box.X} to {box.X + box.Width} on a screen {width} wide.");
        await Axe.AssertNoViolationsAsync(page, "The Log settings panel on a phone");
        await page.Keyboard.PressAsync("Escape");
        await ExpectClosedAndPageFreeAsync(page);
    }
}
