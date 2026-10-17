using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs › Log settings: how long the system log, Events and a file's activity are kept. The three numbers are edited together and saved with one
/// button that shows only while one differs from what is saved; a number the server cannot take is put right when the field is left. Each test has a server of its own.
/// </summary>
public sealed class SystemLogRetentionTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = "/api/v1/suite/settings";
    private const string OperatorSettings = "/api/v1/processing/operator-settings";
    private const float ServerStoppedMs = 20_000;

    private static ILocator Panel(IPage page) => page.GetByRole(AriaRole.Dialog, new() { Name = "Log settings" });

    private static ILocator SystemLog(IPage page) => Panel(page).GetByLabel("System log", new() { Exact = true });

    private static ILocator Events(IPage page) => Panel(page).GetByLabel("Events", new() { Exact = true });

    private static ILocator FileActivity(IPage page) => Panel(page).GetByLabel("File activity", new() { Exact = true });

    private static ILocator Save(IPage page) => Panel(page).GetByRole(AriaRole.Button, new() { Name = "Save retention", Exact = true });

    private static async Task<SystemSession> OpenSettingsAsync(IPage page)
    {
        var system = await SystemSession.OpenAsync(page, "Logs");
        await page.GetByRole(AriaRole.Button, new() { Name = "Log settings", Exact = true }).ClickAsync();
        await Expect(Panel(page)).ToBeVisibleAsync();
        await Expect(FileActivity(page)).ToBeEnabledAsync();
        return system;
    }

    /// <summary>Types <paramref name="typed"/> into the field and leaves it, as a person does.</summary>
    private static async Task TypeAndLeaveAsync(ILocator field, string typed)
    {
        await field.FillAsync(typed);
        await field.BlurAsync();
    }

    [E2EFact]
    public async Task Events_kept_for_0_days_means_until_cleared_and_the_save_sends_every_setting_it_does_not_edit()
    {
        await using var system = await OpenSettingsAsync(await NewPageAsync());
        var (page, requests) = (system.Page, system.Requests);
        var before = await system.ReadAsync(Settings);
        await Expect(Save(page)).ToHaveCountAsync(0);
        await Expect(Events(page)).ToHaveValueAsync(((int)before["activity_retention_days"]!).ToString(System.Globalization.CultureInfo.InvariantCulture));

        await Events(page).FillAsync("0");
        await Expect(Save(page)).ToBeVisibleAsync();
        var mark = requests.Mark();
        await Save(page).ClickAsync();

        await Expect(Panel(page).GetByText("Retention saved.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Save(page)).ToHaveCountAsync(0);
        var sent = Assert.Single(requests.Since(mark));
        Assert.Equal(("PUT", Settings), (sent.Method, sent.PathAndQuery));
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.Equal(0, (int)body["activity_retention_days"]!);
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]));
        foreach (var unchanged in new[] { "log_retention_days", "setup_wizard_state", "app_timezone", "configuration_backup_enabled", "configuration_backup_interval_hours", "configuration_backup_preferred_time" })
        {
            Assert.Equal(before[unchanged]!.ToJsonString(), body[unchanged]!.ToJsonString());
        }

        Assert.Equal(0, (int)(await system.ReadAsync(Settings))["activity_retention_days"]!);
    }

    [E2EFact]
    public async Task A_number_typed_stays_while_the_person_moves_between_the_fields()
    {
        await using var system = await OpenSettingsAsync(await NewPageAsync());
        var page = system.Page;
        await SystemLog(page).FillAsync("45");

        await Events(page).FocusAsync();
        await SystemLog(page).FocusAsync();

        await Expect(SystemLog(page)).ToHaveValueAsync("45");
        await Expect(Save(page)).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task A_number_outside_what_the_server_takes_is_put_right_when_the_field_is_left_and_a_blank_one_keeps_what_was_saved()
    {
        await using var system = await OpenSettingsAsync(await NewPageAsync());
        var page = system.Page;
        var saved = await system.ReadAsync(Settings);
        var savedLog = ((int)saved["log_retention_days"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var savedEvents = ((int)saved["activity_retention_days"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var (typed, shown) in new[] { ("0", "1"), ("99999", "3650"), ("-5", "1"), (string.Empty, "30"), ("45", "45") })
        {
            await TypeAndLeaveAsync(SystemLog(page), typed);
            await Expect(SystemLog(page)).ToHaveValueAsync(shown);
        }

        foreach (var (typed, shown) in new[] { ("99999", "3650"), ("-5", "0"), (string.Empty, savedEvents), ("12", "12") })
        {
            await TypeAndLeaveAsync(Events(page), typed);
            await Expect(Events(page)).ToHaveValueAsync(shown);
        }

        await Expect(Save(page)).ToBeVisibleAsync();
        await TypeAndLeaveAsync(SystemLog(page), savedLog);
        await TypeAndLeaveAsync(Events(page), savedEvents);
        await Expect(Save(page)).ToHaveCountAsync(0);
        Assert.Equal(saved["log_retention_days"]!.ToJsonString(), (await system.ReadAsync(Settings))["log_retention_days"]!.ToJsonString());
    }

    [E2EFact]
    public async Task File_activity_days_are_saved_with_the_same_button_a_blank_or_too_large_one_is_refused_and_the_server_keeps_the_number()
    {
        await using var system = await OpenSettingsAsync(await NewPageAsync());
        var (page, requests) = (system.Page, system.Requests);
        var before = (int)(await system.ReadAsync(OperatorSettings))["file_log_retention_days"]!;
        await Expect(FileActivity(page)).ToHaveValueAsync(before.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var refused in new[] { string.Empty, "3651", "-1" })
        {
            await FileActivity(page).FillAsync(refused);
            await Expect(Save(page)).ToBeDisabledAsync();
        }

        await FileActivity(page).FillAsync("0");
        await Expect(Save(page)).ToBeEnabledAsync();
        var mark = requests.Mark();
        await Save(page).ClickAsync();

        await Expect(Panel(page).GetByText("Retention saved.", new() { Exact = true })).ToBeVisibleAsync();
        var sent = Assert.Single(requests.Since(mark));
        Assert.Equal(("PUT", OperatorSettings), (sent.Method, sent.PathAndQuery));
        Assert.Equal(["csrf_token", "file_log_retention_days"], JsonNode.Parse(sent.Body!)!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(0, (int)JsonNode.Parse(sent.Body!)!["file_log_retention_days"]!);
        Assert.Equal(0, (int)(await system.ReadAsync(OperatorSettings))["file_log_retention_days"]!);

        await FileActivity(page).FillAsync("3650");
        await Events(page).FillAsync("7");
        mark = requests.Mark();
        await Save(page).ClickAsync();

        await Expect(Panel(page).GetByText("Retention saved.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Save(page)).ToHaveCountAsync(0);
        Assert.Equal(["PUT /api/v1/processing/operator-settings", "PUT /api/v1/suite/settings"], requests.Since(mark).Select(request => $"{request.Method} {request.PathAndQuery}").Order(StringComparer.Ordinal));
        Assert.Equal(3650, (int)(await system.ReadAsync(OperatorSettings))["file_log_retention_days"]!);
        Assert.Equal(7, (int)(await system.ReadAsync(Settings))["activity_retention_days"]!);
    }

    [E2EFact]
    public async Task A_save_the_server_cannot_take_says_so_and_keeps_the_numbers_typed()
    {
        await using var system = await OpenSettingsAsync(await NewPageAsync());
        var page = system.Page;
        await Events(page).FillAsync("9");
        await system.Server.StopAsync();

        await Save(page).ClickAsync();

        await Expect(Panel(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = ServerStoppedMs });
        await Expect(Events(page)).ToHaveValueAsync("9");
        await Expect(Save(page)).ToBeEnabledAsync();
    }
}
