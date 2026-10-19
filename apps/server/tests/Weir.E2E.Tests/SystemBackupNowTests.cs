using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Backups › Back up now, and the Last backup line of the schedule: a backup made by hand is a file on the server and a row in the list, the line
/// names when it was made, and with automatic backups on it says whether the schedule is being kept to.
/// </summary>
public sealed class SystemBackupNowTests(E2EServer server) : E2ETestBase(server)
{
    private const string Backups = "/api/v1/suite/configuration-backups";
    private const float ScheduledBackupMs = 30_000;
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);

    /// <summary>The hour a missed backup is first called overdue: the page allows an hour past the scheduled midnight.</summary>
    private const int FirstHourPastGrace = 2;

    private static ILocator RestoreButtons(IPage page) => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Restore the backup taken") });

    private static ILocator Schedule(IPage page) => page.GetByRole(AriaRole.Region, new() { Name = "Schedule", Exact = true });

    private static async Task TurnOnAutomaticBackupsAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Radiogroup, new() { Name = "Back up automatically", Exact = true }).GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save schedule", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Backup schedule saved.", new() { Exact = true })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Back_up_now_makes_one_backup_the_list_shows_it_and_the_buttons_are_off_while_it_is_made()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var (page, requests) = (system.Page, system.Requests);
        await Expect(page.GetByText("No automatic snapshots yet.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Schedule(page)).ToContainTextAsync(new Regex(@"Last backup\s*None yet"));
        Assert.Empty((await system.ReadAsync(Backups))["items"]!.AsArray());
        var buttons = new[] { "Back up now", "Download settings", "Restore from file…" };
        await PageTrail.WatchAsync(
            page,
            "buttons-off",
            "[...document.querySelectorAll('button')].filter(b => ['Back up now','Download settings','Restore from file…'].includes(b.textContent.trim())).length === 3 && [...document.querySelectorAll('button')].filter(b => ['Back up now','Download settings','Restore from file…'].includes(b.textContent.trim())).every(b => b.disabled)");
        var mark = requests.Mark();

        await page.GetByRole(AriaRole.Button, new() { Name = "Back up now", Exact = true }).ClickAsync();

        await Expect(page.GetByText("Backup created.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(RestoreButtons(page)).ToHaveCountAsync(1);
        await Expect(page.GetByText("No automatic snapshots yet.", new() { Exact = true })).ToHaveCountAsync(0);
        Assert.True(await PageTrail.SawAsync(page, "buttons-off"), "The buttons stayed on while the backup was made.");
        foreach (var name in buttons)
        {
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true })).ToBeEnabledAsync();
        }

        var sent = Assert.Single(requests.Since(mark));
        Assert.Equal(("POST", Backups), (sent.Method, sent.PathAndQuery));
        Assert.Equal(["csrf_token"], JsonNode.Parse(sent.Body!)!.AsObject().Select(field => field.Key));
        var listed = await system.ReadAsync(Backups);
        var item = Assert.Single(listed["items"]!.AsArray())!.AsObject();
        Assert.True(File.Exists(Path.Join((string)listed["directory"]!, (string)item["file_name"]!)), "The backup is not a file in the backup folder.");
        await Expect(page.GetByTestId("suite-configuration-backup-directory")).ToHaveTextAsync((string)listed["directory"]!);
    }

    [E2EFact]
    public async Task The_Last_backup_line_names_the_scheduled_backup_and_reads_Up_to_date_until_a_day_has_been_missed()
    {
        // The page runs on a clock the test can move, from before its first timer is set; the server looks for a due backup every second.
        var opened = await NewPageAsync();
        await opened.Clock.InstallAsync();
        await using var system = await SystemSession.OpenAsync(opened, "Backups", ("WEIR_ENV", "development"), ("WEIR_PERIODIC_TASK_SPEEDUP", "60"));
        var page = system.Page;
        await Expect(Schedule(page)).ToContainTextAsync(new Regex(@"Last backup\s*None yet"));
        await Expect(page.GetByText("No automatic snapshots yet.", new() { Exact = true })).ToBeVisibleAsync();

        // Midnight has always passed, so the first backup is due as soon as the schedule is on.
        await page.GetByLabel("At", new() { Exact = true }).FillAsync("00:00");
        await TurnOnAutomaticBackupsAsync(page);

        await Expect(Schedule(page).GetByText("Up to date", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = ScheduledBackupMs });
        await Expect(Schedule(page)).Not.ToContainTextAsync(new Regex(@"Last backup\s*None yet"));
        await Expect(Schedule(page).GetByText("Overdue")).ToHaveCountAsync(0);
        await Expect(RestoreButtons(page)).ToHaveCountAsync(1);
        Assert.False(string.IsNullOrEmpty((string?)(await system.ReadAsync("/api/v1/suite/settings"))["configuration_backup_last_run_at"]));

        // Land on a time of day past the one-hour allowance after midnight, so the result does not depend on when the test runs.
        await page.Clock.FastForwardAsync((long)ThreeDays.TotalMilliseconds);
        var zone = (string?)(await system.ReadAsync("/api/v1/suite/settings"))["app_timezone"];
        var hour = await page.EvaluateAsync<int>("zone => Number(new Intl.DateTimeFormat('en-GB', { timeZone: zone || undefined, hour: 'numeric', hourCycle: 'h23' }).format(Date.now()))", zone);
        if (hour < FirstHourPastGrace)
        {
            await page.Clock.FastForwardAsync((long)TimeSpan.FromHours(FirstHourPastGrace - hour).TotalMilliseconds);
        }

        await Expect(Schedule(page).GetByText("Overdue", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Schedule(page).GetByText("Up to date")).ToHaveCountAsync(0);
    }
}
