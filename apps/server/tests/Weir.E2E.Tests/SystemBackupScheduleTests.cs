using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Backups › Schedule: when Weir backs up its own settings. The choices are drafts until Save schedule, which appears only once one differs
/// from what is saved; the save sends the Logs tab's numbers and the settings it does not edit along with them. Each test has a server of
/// its own, so the backups it makes are not the next test's.
/// </summary>
public sealed class SystemBackupScheduleTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = "/api/v1/suite/settings";

    private static ILocator Every(IPage page) => page.GetByLabel("Every", new() { Exact = true });

    private static ILocator At(IPage page) => page.GetByLabel("At", new() { Exact = true });

    private static ILocator SaveSchedule(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Save schedule", Exact = true });

    private static ILocator Automatically(IPage page, string choice) =>
        page.GetByRole(AriaRole.Radiogroup, new() { Name = "Back up automatically", Exact = true }).GetByRole(AriaRole.Radio, new() { Name = choice, Exact = true });

    private static ILocator Saved(IPage page) => page.GetByText("Backup schedule saved.", new() { Exact = true });

    [E2EFact]
    public async Task Every_offers_six_hours_to_seven_days_and_Save_schedule_shows_only_while_a_choice_differs_from_what_is_saved()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var page = system.Page;

        await Expect(Every(page).Locator("option")).ToHaveTextAsync(["6 hours", "12 hours", "24 hours", "48 hours", "72 hours", "7 days"]);
        await Expect(Every(page)).ToHaveValueAsync("24");
        await Expect(At(page)).ToHaveValueAsync("02:00");
        await Expect(Automatically(page, "Off")).ToBeCheckedAsync();
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);

        await Every(page).SelectOptionAsync("48");
        await Expect(SaveSchedule(page)).ToBeVisibleAsync();
        await Every(page).SelectOptionAsync("24");
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);

        await At(page).FillAsync("03:30");
        await Expect(SaveSchedule(page)).ToBeVisibleAsync();
        await At(page).FillAsync("02:00");
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);

        await Automatically(page, "On").ClickAsync();
        await Expect(SaveSchedule(page)).ToBeVisibleAsync();
        await Automatically(page, "Off").ClickAsync();
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task A_cleared_time_goes_back_to_two_in_the_morning()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var page = system.Page;
        await At(page).FillAsync("05:15");
        await Expect(At(page)).ToHaveValueAsync("05:15");

        await At(page).FillAsync(string.Empty);

        await Expect(At(page)).ToHaveValueAsync("02:00");
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Saving_the_schedule_twice_sends_the_whole_settings_each_time_and_the_server_keeps_what_was_saved()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var (page, requests) = (system.Page, system.Requests);
        var before = await system.ReadAsync(Settings);

        await Automatically(page, "On").ClickAsync();
        await Every(page).SelectOptionAsync("48");
        await At(page).FillAsync("03:30");
        await PageTrail.WatchTextAsync(page, "Saving…");
        var mark = requests.Mark();
        await SaveSchedule(page).ClickAsync();

        await Expect(Saved(page)).ToBeVisibleAsync();
        await Expect(SaveSchedule(page)).ToHaveCountAsync(0);
        Assert.True(await PageTrail.SawAsync(page, "Saving…"), "The button never said it was saving.");
        var first = Assert.Single(requests.Since(mark));
        Assert.Equal("PUT", first.Method);
        Assert.Equal(Settings, first.PathAndQuery);
        var sent = JsonNode.Parse(first.Body!)!.AsObject();
        Assert.False(string.IsNullOrEmpty((string?)sent["csrf_token"]), "The save carries the page's CSRF token.");
        Assert.True((bool)sent["configuration_backup_enabled"]!);
        Assert.Equal(48, (int)sent["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string?)sent["configuration_backup_preferred_time"]);
        foreach (var unchanged in new[] { "setup_wizard_state", "app_timezone", "log_retention_days", "activity_retention_days" })
        {
            Assert.Equal(before[unchanged]!.ToJsonString(), sent[unchanged]!.ToJsonString());
        }

        var saved = await system.ReadAsync(Settings);
        Assert.True((bool)saved["configuration_backup_enabled"]!);
        Assert.Equal(48, (int)saved["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string?)saved["configuration_backup_preferred_time"]);
        Assert.Equal(before["setup_wizard_state"]!.ToJsonString(), saved["setup_wizard_state"]!.ToJsonString());

        await Every(page).SelectOptionAsync("168");
        await Expect(Saved(page)).ToHaveCountAsync(0);
        mark = requests.Mark();
        await SaveSchedule(page).ClickAsync();

        await Expect(Saved(page)).ToBeVisibleAsync();
        var second = Assert.Single(requests.Since(mark));
        Assert.Equal(168, (int)JsonNode.Parse(second.Body!)!["configuration_backup_interval_hours"]!);
        Assert.Equal(168, (int)(await system.ReadAsync(Settings))["configuration_backup_interval_hours"]!);

        await page.ReloadAsync();
        await Expect(Automatically(page, "On")).ToBeCheckedAsync();
        await Expect(Every(page)).ToHaveValueAsync("168");
        await Expect(At(page)).ToHaveValueAsync("03:30");
    }
}
