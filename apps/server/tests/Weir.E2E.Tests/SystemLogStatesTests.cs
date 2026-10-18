using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Logs in each state it can be in: loading, nothing matching the filters, unreadable, and with the live link to Weir dropped.</summary>
public sealed class SystemLogStatesTests(E2EServer server) : E2ETestBase(server)
{
    private const float ReconnectMs = 20_000;
    private const float RecoverMs = 40_000;
    private const string Banner = "Live updates paused: can't reach Weir. Reconnecting…";

    private static ILocator Summary(IPage page) => page.GetByTestId("log-summary");

    private async Task<IPage> OpenSystemAsync()
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Navigation.OpenTabAsync(page, "System", "About");
        return page;
    }

    [E2EFact]
    public async Task The_log_says_it_is_loading_until_its_first_rows_arrive()
    {
        var page = await OpenSystemAsync();
        await PageTrail.WatchTextAsync(page, "Loading the log…");

        await page.GetByRole(AriaRole.Tab, new() { Name = "Logs", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("log-feed")).ToBeVisibleAsync();
        Assert.True(await PageTrail.SawAsync(page, "Loading the log…"), "The list never said it was loading.");
        await Expect(page.GetByText("Loading the log…")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Filters_that_match_nothing_say_so_and_either_Clear_filters_button_brings_the_log_back()
    {
        var page = await OpenSystemAsync();
        await page.GotoAsync($"{BaseUrl}/system?tab=logs&q=nothing-in-the-log-says-this", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var empty = page.GetByTestId("log-empty");
        await Expect(empty).ToContainTextAsync("Nothing in the log matches these filters.");
        await Expect(Summary(page)).ToHaveTextAsync("0 entries matching · live");
        await Expect(page.GetByTestId("log-row")).ToHaveCountAsync(0);

        await empty.GetByRole(AriaRole.Button, new() { Name = "Clear filters →", Exact = true }).ClickAsync();

        await Expect(empty).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("log-row").First).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/system\?tab=logs$"));
        await Expect(page.GetByPlaceholder("Search the log")).ToHaveValueAsync(string.Empty);
    }

    [E2EFact]
    public async Task A_log_that_cannot_be_read_says_so_instead_of_showing_an_empty_list_and_is_read_again_when_Weir_answers()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "About");
        var page = system.Page;
        await system.Server.StopAsync();

        await page.GetByRole(AriaRole.Tab, new() { Name = "Logs", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("logs-card").GetByRole(AriaRole.Alert)).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = ReconnectMs });
        await Expect(page.GetByTestId("log-empty")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("log-row")).ToHaveCountAsync(0);

        await system.Server.RestartAsync(samePort: true);

        await Expect(page.GetByTestId("log-row").First).ToBeVisibleAsync(new() { Timeout = RecoverMs });
        await Expect(page.GetByTestId("logs-card").GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task While_the_live_link_is_down_Logs_does_not_call_itself_live_and_says_so_again_when_it_is_back()
    {
        var page = await OpenSystemAsync();
        await Navigation.OpenLogsAsync(page);
        await Expect(Summary(page)).ToContainTextAsync("· live");
        await Expect(Summary(page)).Not.ToContainTextAsync("live updates paused");
        var banner = page.GetByTestId("live-connection-banner");
        await Expect(banner).ToHaveCountAsync(0);

        await page.Context.SetOfflineAsync(true);

        await Expect(banner).ToHaveTextAsync(Banner, new() { Timeout = ReconnectMs });
        await Expect(Summary(page)).ToContainTextAsync("· live updates paused");
        await Expect(Summary(page).Locator("[data-status]")).ToHaveAttributeAsync("data-status", "attention");

        await page.Context.SetOfflineAsync(false);

        await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
        await Expect(Summary(page)).ToContainTextAsync("· live");
        await Expect(Summary(page)).Not.ToContainTextAsync("live updates paused");
    }

    [E2EFact]
    public async Task While_the_live_link_is_down_About_says_so_and_the_notice_clears_when_it_is_back()
    {
        var page = await OpenSystemAsync();
        var banner = page.GetByTestId("live-connection-banner");
        await Expect(page.GetByTestId("suite-settings-global")).ToBeVisibleAsync();
        await Expect(banner).ToHaveCountAsync(0);

        await page.Context.SetOfflineAsync(true);

        await Expect(banner).ToHaveTextAsync(Banner, new() { Timeout = ReconnectMs });

        await page.Context.SetOfflineAsync(false);

        await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
        await Expect(page.GetByTestId("suite-settings-global")).ToBeVisibleAsync();
    }
}
