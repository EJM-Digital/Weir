using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Backups › restoring a stored backup replaces the server's settings with what the backup holds. Weir asks in a dialog of its own first;
/// leaving it by any way but Replace settings changes nothing, and Replace settings sends the backup that was read and nothing else.
/// </summary>
public sealed class SystemRestoreBackupTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = "/api/v1/suite/settings";
    private const string Backups = "/api/v1/suite/configuration-backups";
    private const string Bundle = "/api/v1/suite/configuration-bundle";

    private static ILocator Dialog(IPage page) => page.GetByTestId("restore-configuration-dialog");

    private static ILocator Restore(IPage page) => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Restore the backup taken") });

    private static async Task SaveSettingsAsync(SystemSession system, int logDays, int everyHours, string at)
    {
        var saved = await system.Admin.PutWithCsrfAsync(
            Settings,
            new JsonObject
            {
                ["app_timezone"] = "UTC",
                ["log_retention_days"] = logDays,
                ["configuration_backup_interval_hours"] = everyHours,
                ["configuration_backup_preferred_time"] = at,
            });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    [E2EFact]
    public async Task Restoring_a_stored_backup_asks_first_and_only_Replace_settings_puts_its_settings_back()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var (page, requests) = (system.Page, system.Requests);
        var browserDialogs = new List<string>();
        page.Dialog += (_, dialog) =>
        {
            if (dialog.Type != "beforeunload")
            {
                browserDialogs.Add(dialog.Message);
            }

            _ = dialog.AcceptAsync();
        };
        await SaveSettingsAsync(system, logDays: 45, everyHours: 48, at: "03:30");
        var made = await system.Admin.PostWithCsrfAsync(Backups);
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var snapshot = await system.Admin.GetAsync($"{Backups}/{(long)made.Fields["id"]!}/download");
        Assert.True(snapshot.Status == HttpStatusCode.OK, snapshot.ToString());
        await SaveSettingsAsync(system, logDays: 60, everyHours: 6, at: "04:45");
        await page.ReloadAsync();
        await Expect(Restore(page)).ToHaveCountAsync(1);
        await Expect(page.GetByLabel("Every", new() { Exact = true })).ToHaveValueAsync("6");
        await Expect(page.GetByLabel("At", new() { Exact = true })).ToHaveValueAsync("04:45");

        // Every way out of the dialog but the button that replaces leaves the settings as they are.
        foreach (var leave in new Func<Task>[]
        {
            () => page.Keyboard.PressAsync("Escape"),
            () => page.GetByRole(AriaRole.Button, new() { Name = "Keep current settings", Exact = true }).ClickAsync(),
        })
        {
            var mark = requests.Mark();
            await Restore(page).ClickAsync();
            await Expect(Dialog(page)).ToBeVisibleAsync();
            await Expect(Dialog(page)).ToContainTextAsync("Replace the settings on this server with this backup?");
            await Expect(Dialog(page)).ToContainTextAsync("This cannot be undone.");

            await leave();

            await Expect(Dialog(page)).ToHaveCountAsync(0);
            requests.AssertNothingSentSince(mark);
            Assert.Equal(60, (int)(await system.ReadAsync(Settings))["log_retention_days"]!);
        }

        await Restore(page).ClickAsync();
        await Expect(Dialog(page)).ToBeVisibleAsync();
        var replaceMark = requests.Mark();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Replace settings", Exact = true }).ClickAsync();

        await Expect(page.GetByText("Configuration restored.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        var sent = Assert.Single(requests.Since(replaceMark));
        Assert.Equal(("PUT", Bundle), (sent.Method, sent.PathAndQuery));
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.Equal(["bundle", "csrf_token"], body.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.True(JsonNode.DeepEquals(body["bundle"], snapshot.Json), "The bundle sent is not the backup that was read.");
        var restored = await system.ReadAsync(Settings);
        Assert.Equal(45, (int)restored["log_retention_days"]!);
        Assert.Equal(48, (int)restored["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string?)restored["configuration_backup_preferred_time"]);

        // The screens show the restored settings at once: the schedule's drafts are what the server now holds.
        await Expect(page.GetByLabel("Every", new() { Exact = true })).ToHaveValueAsync("48");
        await Expect(page.GetByLabel("At", new() { Exact = true })).ToHaveValueAsync("03:30");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save schedule", Exact = true })).ToHaveCountAsync(0);
        Assert.Empty(browserDialogs);
    }
}
