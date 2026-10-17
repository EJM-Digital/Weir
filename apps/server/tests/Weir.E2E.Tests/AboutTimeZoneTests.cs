using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › About › Time zone against the real server: the zones on offer, Save appearing only for a different zone, and every time in Weir following the saved one.</summary>
public sealed class AboutTimeZoneTests(E2EServer server) : E2ETestBase(server)
{
    private const string SaveRequest = "PUT /api/v1/suite/settings";

    private static ILocator Picker(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Time zone", Exact = true });

    private static ILocator Save(IPage page) => page.GetByTestId("schedule-save-timezone");

    /// <summary>The note's clock for each of the last minute and a half, as the browser writes it for this zone: the page redraws it every 30 seconds.</summary>
    private static Task<string[]> RecentClocksAsync(IPage page, string zone) =>
        page.EvaluateAsync<string[]>(
            """
            (zone) => [0, 30, 60, 90].map((ago) => {
              const parts = new Intl.DateTimeFormat('en-GB', { timeZone: zone, weekday: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
                .formatToParts(new Date(Date.now() - ago * 1000));
              const get = (type) => parts.find((part) => part.type === type).value;
              return `Now ${get('weekday')} ${get('hour')}:${get('minute')}. Every time across Weir is in this zone.`;
            })
            """,
            zone);

    [E2EFact]
    public async Task A_different_zone_is_saved_with_the_request_the_page_sends_and_every_time_follows_it()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "System", "About");
        var sent = ChangesSent.Watch(page);
        await Expect(Picker(page)).ToContainTextAsync("UTC");
        await Expect(Save(page)).ToHaveCountAsync(0);

        await Picker(page).ClickAsync();
        var options = page.GetByRole(AriaRole.Option);
        await Expect(options).ToHaveCountAsync(33);
        await Expect(options.Filter(new() { HasText = "Coordinated Universal Time" })).ToHaveAttributeAsync("aria-selected", "true");
        await options.Filter(new() { HasText = "Australia — Sydney" }).ClickAsync();

        await Expect(Picker(page)).ToContainTextAsync("Australia — Sydney");
        await Expect(Save(page)).ToBeVisibleAsync();
        Assert.Empty(await sent.SoFarAsync());
        await Save(page).ClickAsync();

        await Expect(Save(page)).ToHaveCountAsync(0, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(Picker(page)).ToContainTextAsync("Australia — Sydney");
        Assert.Equal([SaveRequest], await sent.SoFarAsync());
        var body = (await sent.BodiesAsync(SaveRequest)).Single();
        Assert.Equal("Australia/Sydney", (string)body["app_timezone"]!);
        Assert.Equal("Australia/Sydney", (await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/suite/settings")).GetProperty("app_timezone").GetString());
        var note = await page.Locator("p.mm-sys-note").Filter(new() { HasText = "Every time across Weir is in this zone." }).InnerTextAsync();
        Assert.Contains(Regex.Replace(note, @"\s+", " "), await RecentClocksAsync(page, "Australia/Sydney"));

        await Picker(page).ClickAsync();
        await options.Filter(new() { HasText = "Coordinated Universal Time" }).ClickAsync();
        await Expect(Save(page)).ToBeVisibleAsync();
        await Picker(page).ClickAsync();
        await options.Filter(new() { HasText = "Australia — Sydney" }).ClickAsync();
        await Expect(Save(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task A_saved_zone_changes_the_times_other_screens_show()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "System", "About");
        await Picker(page).ClickAsync();
        await page.GetByRole(AriaRole.Option).Filter(new() { HasText = "Australia — Sydney" }).ClickAsync();
        await Save(page).ClickAsync();
        await Expect(Save(page)).ToHaveCountAsync(0, new() { Timeout = SecurityTab.ServerAnswerMs });

        await Navigation.OpenTabAsync(page, "Performance", "Cleanup");

        await Expect(CleanupTab.Row(page, CleanupTab.Leftover)).ToBeVisibleAsync();
        await Poll.UntilAsync(async () => (await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Leftover)).GetProperty("last_completed_at").ValueKind != System.Text.Json.JsonValueKind.Null, "the sweep Weir runs when it starts");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var completed = (await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Leftover)).GetProperty("last_completed_at").GetString()!;
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='lastRun']"))
            .ToHaveTextAsync(await AppDates.ShownAsync(page, completed, "Australia/Sydney"));
    }
}
