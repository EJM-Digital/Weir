using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The Dashboard while the window is dragged: it holds still through every size the window passes, and moves again once the size has stopped changing.</summary>
public sealed class DashboardRepeatedResizeTests(E2EServer server) : E2ETestBase(server)
{
    private static readonly Regex Resizing = new(@"\bmm-pipeline-resizing\b");

    [E2EFact]
    public async Task The_Dashboard_stays_still_through_several_sizes_in_a_row_and_settles_after_the_last()
    {
        var page = await NewPageAsync(new ViewportSize { Width = 1280, Height = 800 });
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Expect(page.GetByTestId("pipeline-board")).ToBeVisibleAsync();

        foreach (var width in new[] { 1100, 900, 700 })
        {
            await page.SetViewportSizeAsync(width, 800);
        }

        await Expect(page.GetByTestId("pipeline-board")).ToBeVisibleAsync();
        await Expect(page.Locator("body")).Not.ToHaveClassAsync(Resizing, new() { Timeout = Navigation.UrlAssertMs });
    }
}
