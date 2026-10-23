using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A bookmark to the former Processing page, which had tabs: with no tab, or one Weir no longer has, it lands on the Dashboard, where Overview went.
/// </summary>
public sealed class LegacyProcessingAddressTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task A_bookmark_to_Processing_with_no_tab_lands_on_the_Dashboard()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await page.GotoAsync($"{BaseUrl.TrimEnd('/')}/processing");

        await Expect(page).ToHaveURLAsync($"{BaseUrl.TrimEnd('/')}/", new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByTestId("pipeline-board")).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task A_bookmark_to_a_Processing_tab_that_no_longer_exists_lands_on_the_Dashboard()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await page.GotoAsync($"{BaseUrl.TrimEnd('/')}/processing?tab=reports");

        await Expect(page).ToHaveURLAsync($"{BaseUrl.TrimEnd('/')}/", new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByTestId("pipeline-board")).ToBeVisibleAsync();
    }
}
