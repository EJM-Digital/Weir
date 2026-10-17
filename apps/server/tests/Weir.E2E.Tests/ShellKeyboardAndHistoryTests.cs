using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The shell used without a mouse, and with the browser's own Back and Forward: the skip link, the side menu and the user menu by keyboard, and the places the history keeps.</summary>
public sealed class ShellKeyboardAndHistoryTests(E2EServer server) : E2ETestBase(server)
{
    private static readonly PageGotoOptions DomContentLoaded = new() { WaitUntil = WaitUntilState.DOMContentLoaded };

    [E2EFact]
    public async Task The_first_Tab_offers_the_skip_link_which_moves_focus_to_the_page_s_content()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.GotoAsync($"{BaseUrl}/activity", DomContentLoaded);
        await Expect(page.GetByTestId("activity-page")).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Tab");

        var skip = page.GetByRole(AriaRole.Link, new() { Name = "Skip to content", Exact = true });
        await Expect(skip).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("main#mm-main-content")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Each_place_in_the_side_menu_is_reached_by_Tab_and_opened_by_Enter()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var places = new[] { ("Activity", "/activity", "activity-page"), ("Library", "/library", "library-page"), ("Workflows", "/setup/workflows", "suite-settings-page"), ("System", "/system", "suite-system-page") };

        foreach (var (name, address, ready) in places)
        {
            var link = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex($"^{name}(?:,|$)") });
            await link.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(new Regex($".*{address}$"));
            await Expect(page.GetByTestId(ready)).ToBeVisibleAsync();
            await Expect(link).ToHaveAttributeAsync("aria-current", "page");
        }

        // Tab walks the side menu in the order it is drawn, so no place is skipped.
        await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = true }).FocusAsync();
        var order = new List<string>();
        for (var press = 0; press < 8; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            order.Add(await page.EvaluateAsync<string>("() => document.activeElement?.textContent?.trim().split(/[,\\n]/)[0] ?? ''"));
        }

        Assert.Equal(["Activity", "Library", "Workflows", "Connections", "Rules", "Performance", "System"], order.Take(7));
    }

    [E2EFact]
    public async Task The_user_menu_opens_from_the_keyboard_reaches_both_choices_and_closes_on_Escape_with_focus_back()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var userMenu = page.GetByTestId("user-menu");
        await userMenu.FocusAsync();

        await page.Keyboard.PressAsync("Enter");

        await Expect(userMenu).ToHaveAttributeAsync("aria-expanded", "true");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Change password", Exact = true })).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByTestId("sign-out")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(userMenu).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(page.GetByTestId("sign-out")).ToHaveCountAsync(0);

        await userMenu.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page).ToHaveURLAsync(new Regex(@".*/system\?tab=security$"));
        await Expect(page.GetByTestId("suite-settings-security")).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Back_and_Forward_walk_the_places_and_tabs_visited_and_an_address_opens_its_place_directly()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await Navigation.OpenSidebarAsync(page, "Activity");
        await Expect(page.GetByTestId("activity-page")).ToBeVisibleAsync();
        await Navigation.OpenTabAsync(page, "Connections", "Alerts");
        await Expect(page).ToHaveURLAsync(new Regex(@".*/setup/connections/alerts$"));
        await Navigation.OpenTabAsync(page, "System", "Security");
        await Expect(page).ToHaveURLAsync(new Regex(@".*/system\?tab=security$"));

        await page.GoBackAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@".*/system$"));
        await Expect(page.GetByTestId("suite-settings-global")).ToBeVisibleAsync();
        await page.GoBackAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@".*/setup/connections/alerts$"));
        await Expect(page.GetByTestId("suite-settings-notifications")).ToBeVisibleAsync();
        await page.GoBackAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@".*/setup/connections$"));
        await Expect(page.GetByTestId("suite-settings-media-managers")).ToBeVisibleAsync();
        await page.GoForwardAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@".*/setup/connections/alerts$"));
        await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Alerts", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");

        // An address typed or bookmarked opens that place with that tab, not the Dashboard.
        await page.GotoAsync($"{BaseUrl}/setup/performance/timers", DomContentLoaded);
        await Expect(page.GetByTestId("processing-timers-section")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Weir's timers", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
    }

    [E2EFact]
    public async Task An_address_Weir_does_not_have_says_so_inside_the_shell_and_the_way_out_works_by_keyboard()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await page.GotoAsync($"{BaseUrl}/nowhere/at/all", DomContentLoaded);

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "This page doesn't exist.", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        // The menu stays, so the person is not stranded, and no place in it is marked as the current one.
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync();
        await Expect(page.Locator("a[aria-current='page']")).ToHaveCountAsync(0);
        var way = page.GetByRole(AriaRole.Link, new() { Name = "Go to Dashboard", Exact = true });
        await way.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page).ToHaveURLAsync(new Regex(@".*/(?:$|[?#])"));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
    }
}
