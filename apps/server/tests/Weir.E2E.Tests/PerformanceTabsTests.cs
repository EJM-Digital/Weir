using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Performance › Speed's resolution budget and Performance › Weir's timers against the real server: what each control sends, what the
/// server holds afterwards, and the timers table following the server without a reload.
/// </summary>
public sealed class PerformanceTabsTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = $"{WeirClient.Api}/processing/operator-settings";
    private const string SuiteSettings = $"{WeirClient.Api}/suite/settings";
    private const string SaveRequest = "PUT /api/v1/processing/operator-settings";

    private static readonly string[] CostRows = ["Budget", "An SD file costs", "A 720p file costs", "A 1080p file costs", "A 4K file costs"];

    private static ILocator BudgetSwitch(IPage page) =>
        page.GetByRole(AriaRole.Radiogroup, new() { Name = "Also weigh files by resolution", Exact = true });

    private static ILocator Timers(IPage page) => page.GetByTestId("schedule-timers");

    private static ILocator TimerRow(IPage page, string job) => Timers(page).Locator("tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Rowheader, new() { Name = job, Exact = true }) });

    private static async Task<IPage> OpenTimersAsync(IPage page, string baseUrl)
    {
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await Navigation.OpenTabAsync(page, "Performance", "Weir's timers");
        await Expect(Timers(page)).ToBeVisibleAsync();
        return page;
    }

    private static async Task<string> ShownAsync(IPage page, JsonElement state, string field) =>
        state.GetProperty(field).ValueKind == JsonValueKind.Null ? string.Empty : await AppDates.ShownAsync(page, state.GetProperty(field).GetString()!, "UTC");

    [E2EFact]
    public async Task The_resolution_budget_appears_when_switched_on_and_is_saved_and_hides_again_when_switched_off()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Performance", "Speed");
        var sent = ChangesSent.Watch(page);
        var budget = BudgetSwitch(page);
        var save = page.GetByRole(AriaRole.Button, new() { Name = "Save performance settings", Exact = true });
        await Expect(budget.GetByRole(AriaRole.Radio, new() { Name = "Off", Exact = true })).ToHaveAttributeAsync("aria-checked", "true");
        foreach (var row in CostRows)
        {
            await Expect(page.GetByLabel(row, new() { Exact = true })).ToHaveCountAsync(0);
        }

        await budget.GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true }).ClickAsync();

        foreach (var row in CostRows)
        {
            await Expect(page.GetByLabel(row, new() { Exact = true })).ToBeVisibleAsync();
        }

        Assert.Empty(await sent.SoFarAsync());
        await save.ClickAsync();
        await Expect(save).ToHaveCountAsync(0, new() { Timeout = SecurityTab.ServerAnswerMs });
        Assert.Equal([SaveRequest], await sent.SoFarAsync());
        var saved = (await sent.BodiesAsync(SaveRequest)).Single();
        Assert.True((bool)saved["runner_budget_enabled"]!);
        Assert.True((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("runner_budget_enabled").GetBoolean());

        await budget.GetByRole(AriaRole.Radio, new() { Name = "Off", Exact = true }).ClickAsync();

        foreach (var row in CostRows)
        {
            await Expect(page.GetByLabel(row, new() { Exact = true })).ToHaveCountAsync(0);
        }

        await save.ClickAsync();
        await Expect(save).ToHaveCountAsync(0, new() { Timeout = SecurityTab.ServerAnswerMs });
        var bodies = await sent.BodiesAsync(SaveRequest);
        Assert.Equal(2, bodies.Count);
        Assert.False((bool)bodies[1]["runner_budget_enabled"]!);
        Assert.False((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("runner_budget_enabled").GetBoolean());
    }

    [E2EFact]
    public async Task The_timers_table_says_when_each_job_runs_and_when_it_last_and_next_does()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await OpenTimersAsync(page, BaseUrl);
        await Poll.UntilAsync(async () => (await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Leftover)).GetProperty("last_completed_at").ValueKind != JsonValueKind.Null, "the sweep Weir runs when it starts");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(Timers(page)).ToBeVisibleAsync();
        await Expect(Timers(page).Locator("thead th")).ToHaveTextAsync(["Job", "When", "Last run", "Next run", "Where to change it"]);

        var leftover = await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Leftover);
        var row = TimerRow(page, CleanupTab.LeftoverName);
        await Expect(row.Locator("td[data-col='when']")).ToHaveTextAsync("Every hour");
        await Expect(row.Locator("td[data-col='lastRun']")).ToHaveTextAsync(await ShownAsync(page, leftover, "last_completed_at"));
        await Expect(row.Locator("td[data-col='nextRun']")).ToHaveTextAsync(leftover.GetProperty("next_run_at").ValueKind == JsonValueKind.Null ? "—" : await ShownAsync(page, leftover, "next_run_at"));
        await Expect(row.GetByRole(AriaRole.Link, new() { Name = "Change in Cleanup", Exact = true })).ToHaveAttributeAsync("href", "/setup/performance/cleanup");

        var unclaimed = TimerRow(page, CleanupTab.UnclaimedName);
        await Expect(unclaimed.Locator("td[data-col='when']")).ToHaveTextAsync("Off");
        await Expect(unclaimed.Locator("td[data-col='nextRun']")).ToHaveTextAsync("—");

        var backup = TimerRow(page, "Settings backup");
        await Expect(backup.Locator("td[data-col='when']")).ToHaveTextAsync("Off");
        await Expect(backup.Locator("td[data-col='lastRun']")).ToHaveTextAsync("Not yet");
        await Expect(backup.Locator("td[data-col='nextRun']")).ToHaveTextAsync("—");
        await Expect(backup.GetByRole(AriaRole.Link, new() { Name = "Change in Backups", Exact = true })).ToHaveAttributeAsync("href", "/system?tab=backups");
    }

    [E2EFact]
    public async Task The_timers_follow_a_change_made_elsewhere_without_a_reload()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await OpenTimersAsync(page, BaseUrl);
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var leftover = TimerRow(page, CleanupTab.LeftoverName);
        var unclaimed = TimerRow(page, CleanupTab.UnclaimedName);
        var backup = TimerRow(page, "Settings backup");
        await Expect(leftover.Locator("td[data-col='when']")).ToHaveTextAsync("Every hour");

        var cleanup = await admin.PutWithCsrfAsync(Settings, new JsonObject
        {
            ["work_temp_stale_sweep_enabled"] = false,
            ["unclaimed_handback_cleanup_enabled"] = true,
            ["unclaimed_handback_cleanup_interval_seconds"] = 5400,
        });
        Assert.True(cleanup.Status == System.Net.HttpStatusCode.OK, cleanup.ToString());

        await Expect(leftover.Locator("td[data-col='when']")).ToHaveTextAsync("Off");
        await Expect(leftover.Locator("td[data-col='nextRun']")).ToHaveTextAsync("—");
        await Expect(unclaimed.Locator("td[data-col='when']")).ToHaveTextAsync("Every 90 minutes");

        var backups = await admin.PutWithCsrfAsync(SuiteSettings, new JsonObject
        {
            ["setup_wizard_state"] = "skipped",
            ["app_timezone"] = "UTC",
            ["log_retention_days"] = 30,
            ["configuration_backup_enabled"] = true,
            ["configuration_backup_interval_hours"] = 48,
            ["configuration_backup_preferred_time"] = "03:30",
        });
        Assert.True(backups.Status == System.Net.HttpStatusCode.OK, backups.ToString());

        await Expect(backup.Locator("td[data-col='when']")).ToHaveTextAsync("Every 2 days at 03:30");
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }
}
