using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System opens on the tab its address names, takes the names older addresses used, and falls back to About for one it does not know.</summary>
public sealed class SystemTabAddressTests(E2EServer server) : E2ETestBase(server)
{
    private static readonly (string Query, string Tab, string Section)[] Addresses =
    [
        ("tab=backups", "Backups", "suite-settings-backup-tab"),
        ("tab=backup", "Backups", "suite-settings-backup-tab"),
        ("tab=BACKUPS", "Backups", "suite-settings-backup-tab"),
        ("tab=security", "Security", "suite-settings-security"),
        ("tab=logs", "Logs", "log-feed"),
        ("tab=history", "Logs", "log-feed"),
        ("tab=about", "About", "suite-settings-global"),
        ("tab=upgrade", "About", "suite-settings-global"),
        ("tab=nothing-weir-has", "About", "suite-settings-global"),
        ("tab=", "About", "suite-settings-global"),
        ("", "About", "suite-settings-global"),
    ];

    [E2EFact]
    public async Task Each_name_in_the_address_opens_its_tab_and_an_unknown_name_opens_About()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        foreach (var (query, tab, section) in Addresses)
        {
            await page.GotoAsync($"{BaseUrl}/system{(query.Length == 0 ? string.Empty : "?" + query)}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            await Expect(page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
            await Expect(page.GetByTestId(section)).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Tab, new() { Selected = true })).ToHaveCountAsync(1);
        }
    }

    [E2EFact]
    public async Task Choosing_a_tab_puts_its_name_in_the_address_and_takes_the_log_filters_off()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.GotoAsync($"{BaseUrl}/system?tab=logs&source=job&level=error&q=anything", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Logs", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");

        await page.GetByRole(AriaRole.Tab, new() { Name = "Backups", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/system\?tab=backups$"));

        await page.GetByRole(AriaRole.Tab, new() { Name = "About", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/system$"));
        await Expect(page.GetByTestId("suite-settings-global")).ToBeVisibleAsync();
    }
}
