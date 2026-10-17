using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Core.Updates;

namespace Weir.LiveAudit;

// The setup areas, Activity and the Logs jobs, and every System tab.
internal sealed partial class LiveAudit
{
    /// <summary>Each setup area with its tabs in order, and the test id of the panel each tab shows.</summary>
    private static readonly (string Area, (string Tab, string TestId)[] Tabs)[] SetupAreas =
    [
        ("Workflows", [("File paths", "processing-libraries-section"), ("Schedule", "processing-schedules-section")]),
        (
            "Connections",
            [
                ("Media managers", "suite-settings-media-managers"),
                ("Download clients", "suite-settings-download-clients-tab"),
                ("Alerts", "suite-settings-notifications"),
            ]),
        ("Rules", [("Profiles", "processing-rule-set-workspace"), ("Playback devices", "processing-direct-play-section")]),
        (
            "Performance",
            [
                ("Speed", "processing-process-settings"),
                ("Cleanup", "processing-maintenance-section"),
                ("Weir's timers", "processing-timers-section"),
            ]),
    ];

    private static readonly string[] ActivityChips =
    [
        "All",
        "In progress",
        "Finished",
        "Needs you",
        "On hold",
        "Skipped",
        "Failed",
        "Kept",
    ];

    private async Task SettingsTabsAsync()
    {
        // Each setup area is an entry of its own in the side menu, with its tabs across the top of the page.
        foreach (var (area, tabs) in SetupAreas)
        {
            await OpenSidebarAsync(area);
            await VisibleAsync(page.GetByTestId("suite-settings-page"), $"{area} page");
            await VisibleAsync(
                page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1, Name = area, Exact = true }),
                $"{area} page title");
            var expectedTabs = tabs.Select(tab => tab.Tab).ToList();
            Require(
                (await TabLabelsAsync("setup-area-tabs")).SequenceEqual(expectedTabs),
                $"{area} does not offer the tabs [{string.Join(", ", expectedTabs)}]");
            foreach (var (tab, testId) in tabs)
            {
                await OpenTabAsync(area, tab);
                await VisibleAsync(page.GetByTestId(testId), $"{area} › {tab} panel");
                await WorkflowAndScheduleChecksAsync(tab);
            }
        }

        Require(
            await page.GetByTestId("settings-section-tabs").CountAsync() == 0,
            "Settings still has a row of tabs of its own");
        await ScreenshotAsync("settings");
        Record("setup areas: every tab of Workflows, Connections, Rules and Performance");
    }

    private async Task WorkflowAndScheduleChecksAsync(string tab)
    {
        if (tab == "File paths")
        {
            await VisibleAsync(
                page.GetByTestId("workflow-kind-badge").First,
                "each workflow says whether it is Weir only or linked to a media manager");
            var editButtons = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit", Exact = true });
            if (await editButtons.CountAsync() > 0)
            {
                await ClickAsync(editButtons.First, "open workflow editor");
                await VisibleAsync(page.GetByTestId("processing-library-form"), "workflow form");
                var cancel = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true });
                if (await cancel.CountAsync() > 0)
                {
                    await ClickAsync(cancel.Last, "cancel workflow editor");
                }
            }
        }
        else if (tab == "Schedule")
        {
            // A week per library, which says where the time zone they are read in is changed.
            await VisibleAsync(
                page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Change the time zone", Exact = true }),
                "link to the time zone in System › About");
            Require(
                await page.GetByTestId("schedule-library-row").CountAsync() > 0,
                "no library weeks on Schedule");
        }
    }

    /// <summary>
    /// Every kind-of-file chip on Activity in order, without counts: those on the title line, then the ones folded
    /// into More, which is opened to read them and closed again.
    /// </summary>
    private async Task<List<string>> ActivityChipLabelsAsync()
    {
        var group = page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Show" });
        var onLine = await group.Locator("button.mm-segmented__option:not(.mm-more__button)").AllInnerTextsAsync();
        var more = group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "More" });
        IReadOnlyList<string> folded = [];
        if (await more.CountAsync() > 0)
        {
            await ClickAsync(more, "open the More kinds of file menu");
            var menu = page.GetByRole(AriaRole.Menu, new PageGetByRoleOptions { Name = "More kinds of file" });
            await VisibleAsync(menu, "More kinds of file menu");
            folded = await menu.GetByRole(AriaRole.Menuitem).AllInnerTextsAsync();
            await page.Keyboard.PressAsync("Escape");
        }

        return [.. onLine.Concat(folded).Select(text => TrailingCount().Replace(text.Trim(), ""))];
    }

    private async Task ActivityAndJobsAsync()
    {
        // Activity: every file Weir has touched, with the open file's record beside the list.
        await OpenSidebarAsync("Activity");
        await VisibleAsync(page.GetByTestId("activity-page"), "Activity page");
        // Needs you is every file waiting on a person, as the sidebar's badge counts them, in whichever group it
        // is; a skip is its own neutral group rather than counting as Failed, on_hold sits under On hold, and Kept
        // lists the files the owner chose to keep without processing.
        var chipLabels = await ActivityChipLabelsAsync();
        Require(
            chipLabels.SequenceEqual(ActivityChips),
            $"Activity shows [{string.Join(", ", chipLabels)}], not [{string.Join(", ", ActivityChips)}] in order");
        // A fresh install has no files, so assert whichever of the two states is real, and never that the page
        // rendered nothing at all.
        if (await page.GetByTestId("activity-detail").CountAsync() > 0)
        {
            await VisibleAsync(page.GetByTestId("activity-detail"), "Activity open file");
        }
        else
        {
            Require(
                await page.GetByText("Nothing yet.").CountAsync() > 0
                || await page.GetByText("No file matches").CountAsync() > 0,
                "Activity showed neither files nor its empty state");
        }

        var search = page.GetByRole(AriaRole.Searchbox, new PageGetByRoleOptions { Name = "Find a file" });
        await search.FillAsync("audit");
        await search.PressAsync("Enter");
        await VisibleAsync(page.GetByTestId("activity-page"), "Activity after a search");
        await ScreenshotAsync("activity");

        await OpenLogsAsync("Jobs");
        await VisibleAsync(page.GetByTestId("log-feed"), "Logs jobs list");
        await ScreenshotAsync("logs-jobs");
        Record("Activity: every file and its record; Logs: Weir's jobs");
    }

    private async Task SystemInstanceAndSetupAsync()
    {
        await OpenSidebarAsync("System");
        await VisibleAsync(page.GetByTestId("suite-system-page"), "System page");
        var labels = await TabLabelsAsync("system-section-tabs");
        Require(
            labels.SequenceEqual(["About", "Backups", "Security", "Logs"]),
            $"System tabs are [{string.Join(", ", labels)}]");
        await VisibleAsync(page.GetByTestId("suite-settings-global"), "System › About");
        Require(
            await page.GetByText("Media tools", new PageGetByTextOptions { Exact = true }).CountAsync() > 0,
            "runtime facts are missing from About");
        // The packaged build must still link out to source and licence, not just the dev build.
        Require(
            await page.GetByTestId("about-source-code-link").GetAttributeAsync("href") == ProjectLocation.RepositoryUrl,
            "About is missing the Source code link");
        Require(
            await page.GetByText("AGPL-3.0-or-later").CountAsync() > 0,
            "About is missing the licence name");
        // Display density was removed in 3.2 and must not come back.
        Require(
            await page.GetByText("Display density").CountAsync() == 0,
            "Display density is back");
        Require(
            await page.Locator("html").GetAttributeAsync("data-mm-density") is null,
            "the page still carries a display density");
        await VisibleAsync(page.GetByTestId("suite-settings-upgrade-tab"), "Upgrade section");
        // The Windows package asks the tray (Check now, Download update, Restart and apply); any other install re-reads the status.
        var updateButtons = page.GetByTestId("suite-settings-update-actions");
        if (await updateButtons.CountAsync() > 0)
        {
            await VisibleAsync(updateButtons, "update buttons");
            Require(
                await updateButtons.GetByRole(AriaRole.Button).CountAsync() == 3,
                "the update buttons are not Check now, Download update and Restart and apply");
        }
        else
        {
            await ClickAsync(
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check again →", Exact = true }),
                "refresh upgrade status");
        }

        // Exercise the wizard's supported re-entry path, then leave it with the safe skip action so the disposable
        // audit account remains usable.
        await ClickAsync(page.GetByTestId("suite-settings-open-setup-wizard"), "open setup wizard from System");
        await VisibleAsync(page.GetByTestId("setup-wizard-skip"), "re-entered setup wizard");
        await VisibleAsync(
            page.GetByText("How do your downloads reach Weir?"),
            "re-entered setup wizard asks how downloads reach Weir");
        Require(
            await page.GetByText("Display density").CountAsync() == 0,
            "Display density is back in the setup wizard");
        await ClickAsync(page.GetByTestId("setup-wizard-skip"), "skip re-entered setup wizard");
        await page.WaitForURLAsync(
            url => !url.Contains("/setup-wizard", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = AuditConfig.TimeoutMs });
        await OpenSidebarAsync("System");
        await VisibleAsync(page.GetByTestId("suite-settings-global"), "return to System › About");
        await ScreenshotAsync("system-instance");
        Record("System › About: time zone, runtime facts, update buttons, and wizard re-entry");
    }

    private async Task SystemBackupsLogsSecurityAsync()
    {
        await OpenTabAsync("System", "Backups");
        await VisibleAsync(page.GetByTestId("suite-settings-backup-tab"), "System backups panel");
        var downloadSettings = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Download settings", Exact = true });
        Require(await downloadSettings.CountAsync() > 0, "configuration download control missing");
        var download = await page.RunAndWaitForDownloadAsync(
            () => downloadSettings.ClickAsync(),
            new PageRunAndWaitForDownloadOptions { Timeout = AuditConfig.TimeoutMs });
        Require(
            download.SuggestedFilename.EndsWith(".json", StringComparison.Ordinal),
            "configuration export is not JSON");

        await OpenLogsAsync("Server");
        var logs = await VisibleAsync(page.GetByTestId("logs-card"), "server log panel");
        await ClickAsync(
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log settings", Exact = true }),
            "open the Log settings");
        await VisibleAsync(
            page.GetByText("Server diagnostics", new PageGetByTextOptions { Exact = true }),
            "server diagnostics disclosure");
        await VisibleAsync(
            page.GetByText("How long things are kept", new PageGetByTextOptions { Exact = true }),
            "how long things are kept");
        await page.Keyboard.PressAsync("Escape");
        await page.GetByRole(AriaRole.Searchbox, new PageGetByRoleOptions { Name = "Search the log" }).FillAsync("audit");
        var level = page.GetByTestId("logs-level-picker");
        await ClickAsync(level, "open the level picker");
        // The picker offers only the levels the log has entries for, so this takes the first of those.
        await ClickAsync(
            await VisibleAsync(page.GetByRole(AriaRole.Option).First, "a level the log has entries for"),
            "narrow the log to a level");
        await ClickAsync(level, "close the level picker");
        await ClickAsync(logs.GetByTestId("logs-export"), "open the log export menu");
        var logDownload = await page.RunAndWaitForDownloadAsync(
            () => page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { NameRegex = new Regex("Whole server log") }).ClickAsync(),
            new PageRunAndWaitForDownloadOptions { Timeout = AuditConfig.TimeoutMs });
        Require(
            logDownload.SuggestedFilename.EndsWith(".log", StringComparison.Ordinal),
            "the whole server log did not download as a .log file");

        await OpenTabAsync("System", "Security");
        await VisibleAsync(page.GetByTestId("suite-settings-security"), "System security panel");
        await VisibleAsync(
            page.GetByText("How sign-in is protected", new PageGetByTextOptions { Exact = true }),
            "how sign-in is protected");
        await VisibleAsync(
            page.GetByText("Active sessions", new PageGetByTextOptions { Exact = true }),
            "active sessions");
        await VisibleAsync(
            page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Change password", Exact = true }),
            "change-password controls");
        await ScreenshotAsync("system-security");
        Record("System backup/export, server log filters, and security/session posture");
    }

    [GeneratedRegex(@"\s+[\d,]+$")]
    private static partial Regex TrailingCount();
}
