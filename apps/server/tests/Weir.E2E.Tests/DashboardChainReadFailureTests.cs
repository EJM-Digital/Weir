using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Health lists each workflow with its own verdict, and a workflow whose folder-chain read failed says it could not be checked rather than
/// that it is fine. A real Weir does not fail that read on demand, so this one request is dropped as a network that fell over drops it, and
/// every other request goes through to the real server.
/// </summary>
public sealed class DashboardChainReadFailureTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    [E2EFact]
    public async Task A_workflow_whose_folder_chain_read_fails_says_it_could_not_be_checked()
    {
        var page = await NewPageAsync(new ViewportSize { Width = 1600, Height = 1000 });
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.RouteAsync(new Regex(@"/api/v1/processing/libraries/\d+/folder-chain$"), route => route.AbortAsync());

        await page.GotoAsync(BaseUrl);

        var rows = page.GetByTestId("live-health").GetByRole(AriaRole.Region, new() { Name = "Workflows", Exact = true }).GetByRole(AriaRole.Listitem);
        await Expect(rows.Filter(new() { HasText = "TV" })).ToContainTextAsync("Couldn't check", new() { Timeout = PushMs });
    }
}
