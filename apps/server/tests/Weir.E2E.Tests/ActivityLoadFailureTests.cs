using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Activity when Weir cannot read the file list: it says so in plain words instead of showing an empty list.</summary>
public sealed class ActivityLoadFailureTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task When_the_file_list_cannot_be_read_Activity_says_so_instead_of_showing_an_empty_list()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.RouteAsync(new Regex(@"/api/v1/processing/files\?"), route => route.FulfillAsync(new()
        {
            Status = 500,
            ContentType = "application/json",
            Body = """{"detail":"The file list could not be read."}""",
        }));

        await Navigation.OpenSidebarAsync(page, "Activity");

        var alert = page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "your file activity" });
        await Expect(alert).ToHaveTextAsync("Weir couldn't load your file activity. Reload the page to try again.", new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByText("Reading activity…", new() { Exact = true })).ToHaveCountAsync(0);
    }
}
