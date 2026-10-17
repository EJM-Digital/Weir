using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The setup areas: the row of tabs and how it is moved along, how each tab says it saves, and the question asked before unsaved edits are left.</summary>
public sealed class SetupAreaTests(E2EServer server) : E2ETestBase(server)
{
    private const string Instant = "Changes save as soon as you make them.";
    private const string Explicit = "Nothing changes until you press Save.";

    [E2EFact]
    public async Task The_arrow_keys_Home_and_End_move_along_the_tabs_and_choose_each_with_Tab_reaching_only_the_chosen_one()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Media managers");
        var tabs = new[] { "Media managers", "Download clients", "Alerts" };

        foreach (var (key, expected) in new[]
                 {
                     ("ArrowRight", 1), ("ArrowRight", 2), ("ArrowRight", 0), ("ArrowLeft", 2), ("Home", 0), ("End", 2), ("ArrowLeft", 1),
                 })
        {
            await page.Keyboard.PressAsync(key);
            var chosen = Tab(page, tabs[expected]);
            await Expect(chosen).ToHaveAttributeAsync("aria-selected", "true");
            await Expect(chosen).ToBeFocusedAsync();
            for (var other = 0; other < tabs.Length; other++)
            {
                await Expect(Tab(page, tabs[other])).ToHaveAttributeAsync("tabindex", other == expected ? "0" : "-1");
            }
        }

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/setup/connections/download-clients$"));
    }

    [E2EFact]
    public async Task Every_tab_of_setup_says_whether_it_saves_as_you_go_or_waits_for_Save()
    {
        using var manager = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        // Media managers, Download clients and Alerts say it on their rows, so each has one.
        var connected = await admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections", new JsonObject { ["kind"] = "sonarr", ["base_url"] = manager.BaseUrl, ["api_key"] = manager.ApiKey, ["enabled"] = true });
        Assert.True(connected.Status == HttpStatusCode.Created, connected.ToString());
        var downloader = await admin.PostWithCsrfAsync($"{WeirClient.Api}/download-clients/connections", new JsonObject { ["kind"] = "qbittorrent", ["base_url"] = "http://qbittorrent.invalid:8080", ["username"] = "admin", ["password"] = "adminadmin", ["enabled"] = true });
        Assert.True(downloader.Status == HttpStatusCode.Created, downloader.ToString());
        var alert = await admin.PostWithCsrfAsync($"{WeirClient.Api}/suite/notification-channels", new JsonObject { ["label"] = "E2E note alert", ["provider"] = "webhook", ["url"] = "https://alerts.example.test/hook", ["events"] = new JsonArray("job_failed"), ["enabled"] = true });
        Assert.True(alert.Status == HttpStatusCode.Created, alert.ToString());
        try
        {
            await AssertNotesAsync(page);
        }
        finally
        {
            await admin.DeleteWithCsrfAsync($"{WeirClient.Api}/suite/notification-channels/{(long)alert.Fields["id"]!}");
            await ManagerConnections.RemoveAsync(admin, (long)connected.Fields["id"]!);
            await admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/download-clients/connections/{(long)downloader.Fields["id"]!}");
        }
    }

    private static async Task AssertNotesAsync(IPage page)
    {
        foreach (var (area, tab, words) in new[]
                 {
                     ("Workflows", "File paths", Instant),
                     ("Workflows", "Schedule", Explicit),
                     ("Connections", "Media managers", Instant),
                     ("Connections", "Download clients", Instant),
                     ("Connections", "Alerts", Instant),
                     ("Rules", "Profiles", Explicit),
                     ("Rules", "Playback devices", Explicit),
                     ("Performance", "Speed", Explicit),
                     ("Performance", "Cleanup", Instant),
                 })
        {
            await Navigation.OpenTabAsync(page, area, tab);
            await Expect(page.GetByTestId("settings-save-model").First).ToHaveTextAsync(words);
        }
    }

    [E2EFact]
    public async Task Leaving_a_tab_with_hours_drawn_and_not_saved_asks_first_and_the_question_is_answered_by_Stay_Escape_or_Leave()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await ScheduleTabTests.CreateAsync(admin, "E2E Guarded hours", string.Empty);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var dialog = page.GetByTestId("settings-unsaved-changes");
            var editor = page.GetByTestId("schedule-library-editor");
            await ScheduleTabTests.Row(page, "E2E Guarded hours").GetByRole(AriaRole.Button, new() { Name = "Change hours" }).ClickAsync();
            await page.GetByTestId("schedule-cell-1-5").ClickAsync();

            // Going back with the browser asks too.
            await page.GoBackAsync();
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" }).ClickAsync();
            await Expect(editor).ToBeVisibleAsync();

            // Another tab asks. Keep editing and Escape stay, and the edit is still there.
            await Tab(page, "File paths").ClickAsync();
            await Expect(dialog).ToContainTextAsync("You have unsaved changes to E2E Guarded hours's hours. Leave without saving?");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" }).ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(Tab(page, "Schedule")).ToHaveAttributeAsync("aria-selected", "true");
            await Tab(page, "File paths").ClickAsync();
            await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("schedule-cell-1-5")).ToHaveAttributeAsync("aria-pressed", "false");

            // Another area asks the same question.
            await Navigation.OpenSidebarAsync(page, "Connections");
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" }).ClickAsync();
            await Expect(editor).ToBeVisibleAsync();

            // Leave without saving moves on and the drawn hour is gone for good.
            await Tab(page, "File paths").ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Leave without saving" }).ClickAsync();
            await Expect(Tab(page, "File paths")).ToHaveAttributeAsync("aria-selected", "true");
            Assert.Equal(string.Empty, (string)(await WorkflowEditor.ReadAsync(admin, id))["schedule_grid"]!);
            await Tab(page, "Schedule").ClickAsync();
            await Expect(editor).ToHaveCountAsync(0);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task Leaving_the_workflow_editor_with_a_change_not_saved_asks_first_and_a_click_outside_the_question_keeps_editing()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E Guarded workflow", string.Empty, string.Empty);
        try
        {
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
            var form = await WorkflowEditor.OpenEditAsync(page, id);
            var dialog = page.GetByTestId("settings-unsaved-changes");
            await WorkflowEditor.Text(form, "Name").FillAsync("E2E Guarded workflow, renamed");

            await Navigation.OpenSidebarAsync(page, "Connections");
            await Expect(dialog).ToContainTextAsync("You have unsaved changes to E2E Guarded workflow. Leave without saving?");
            await Dialogs.ClickOutsideAsync(dialog);
            await Expect(dialog).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(WorkflowEditor.Text(form, "Name")).ToHaveValueAsync("E2E Guarded workflow, renamed");

            await Navigation.OpenSidebarAsync(page, "Connections");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Leave without saving" }).ClickAsync();
            await Expect(Tab(page, "Media managers")).ToHaveAttributeAsync("aria-selected", "true");
            Assert.Equal("E2E Guarded workflow", (string)(await WorkflowEditor.ReadAsync(admin, id))["name"]!);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    private static ILocator Tab(IPage page, string name) => page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });
}
