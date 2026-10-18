using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A screen whose code the browser cannot fetch, as after an update that replaced the files an open tab still points at: the page says
/// something went wrong in plain words, shows the error for a bug report, and Reload Weir and the Dashboard link both get out of it.
/// </summary>
public sealed class RouteFailureTests(E2EServer server) : E2ETestBase(server)
{
    private const string ActivityCode = "**/assets/activity-page-*.js";

    [E2EFact]
    public async Task A_screen_that_cannot_be_loaded_says_so_with_its_error_and_Reload_Weir_brings_it_back()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var gone = true;
        await page.RouteAsync(ActivityCode, async route =>
        {
            if (gone)
            {
                await route.FulfillAsync(new() { Status = 404, ContentType = "text/plain", Body = "Not found" });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        await Navigation.OpenSidebarAsync(page, "Activity");

        var heading = page.GetByRole(AriaRole.Heading, new() { Name = "Something went wrong", Exact = true });
        await Expect(heading).ToBeVisibleAsync();
        await Expect(page.GetByText("Weir hit a screen error before it could finish loading this view.", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Go to the Dashboard", Exact = true })).ToHaveAttributeAsync("href", "/");
        // Focus moves to the message, so a keyboard or screen reader user is told something changed.
        await Expect(page.Locator("#mm-main-content").Filter(new() { Has = heading })).ToBeFocusedAsync();
        // The error is kept behind "Show technical details" for a bug report, and says which file the browser could not fetch.
        var details = page.Locator("details");
        await Expect(details).ToHaveJSPropertyAsync("open", false);
        await details.GetByText("Show technical details", new() { Exact = true }).ClickAsync();
        await Expect(details.Locator("pre")).ToContainTextAsync(new Regex(@"Failed to fetch dynamically imported module: .*/assets/activity-page-.*\.js"));
        await Axe.AssertNoViolationsAsync(page, "The screen that could not be loaded");

        gone = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Reload Weir", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("activity-page")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(heading).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Go_to_the_Dashboard_leaves_a_screen_that_cannot_be_loaded_for_the_Dashboard()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.RouteAsync(ActivityCode, route => route.FulfillAsync(new() { Status = 404, ContentType = "text/plain", Body = "Not found" }));
        await Navigation.OpenSidebarAsync(page, "Activity");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Something went wrong", Exact = true })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Go to the Dashboard", Exact = true }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@".*/(?:$|[?#])"));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
    }
}
