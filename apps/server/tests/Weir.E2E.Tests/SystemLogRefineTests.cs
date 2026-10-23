using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Logs: Refine opens the narrower filters, and the chip that says the log is narrowed to one job is there only when it is.</summary>
public sealed class SystemLogRefineTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Refine_opens_its_filters_and_shows_no_job_chip_while_the_log_is_not_narrowed_to_a_job()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenLogsAsync(page);

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Refine") }).ClickAsync();

        await Expect(page.GetByTestId("logs-refine")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("logs-job-filter")).ToHaveCountAsync(0);
    }
}
