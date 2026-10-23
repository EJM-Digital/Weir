using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › About › Updates on the Windows install: how the tray handles updates. Every choice saves as it is made, with nothing to confirm, and what the
/// page shows is what the server holds. Each test has a server of its own, set up as the Windows package is.
/// </summary>
public sealed class SystemUpdatePreferencesTests(E2EServer server) : E2ETestBase(server)
{
    // The Updates card waits for Weir's own look at GitHub, which here goes nowhere, before it shows the preferences.
    private const float SlowCheckMs = 30_000;
    private const string UpdateSettings = "/api/v1/suite/update-settings";

    private static readonly Dictionary<string, string> ModeWords = new() { ["Auto"] = "Auto", ["DownloadOnly"] = "Download only", ["NotifyOnly"] = "Notify only" };

    private static ILocator Mode(IPage page, string name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Update mode", Exact = true }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static ILocator Startup(IPage page, string choice) =>
        page.GetByRole(AriaRole.Radiogroup, new() { Name = "Check for updates on startup", Exact = true }).GetByRole(AriaRole.Radio, new() { Name = choice, Exact = true });

    private static ILocator Checking(IPage page, string choice) =>
        page.GetByRole(AriaRole.Radiogroup, new() { Name = "Check for updates", Exact = true }).GetByRole(AriaRole.Radio, new() { Name = choice, Exact = true });

    private static ILocator Every(IPage page) => page.GetByLabel("Check every", new() { Exact = true });

    private static ILocator Saved(IPage page) => page.GetByText("Update settings saved.", new() { Exact = true });

    private async Task<SystemSession> OpenAsync() =>
        await SystemSession.OpenAsync(await NewPageAsync(), await E2EServer.StartWindowsInstallAsync(), "About");

    private static JsonObject Sent(SentRequest request) => JsonNode.Parse(request.Body!)!.AsObject();

    [E2EFact]
    public async Task Each_mode_saves_at_once_says_what_it_does_and_the_server_holds_it()
    {
        await using var system = await OpenAsync();
        var (page, requests) = (system.Page, system.Requests);
        var before = await system.ReadAsync(UpdateSettings);
        await Expect(Mode(page, ModeWords[(string)before["mode"]!])).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Saved(page)).ToHaveCountAsync(0);

        foreach (var (name, value, summary) in new[]
        {
            ("Auto", "Auto", "Installs once Weir has been idle 5 minutes."),
            ("Download only", "DownloadOnly", "Installs when you restart Weir."),
            ("Notify only", "NotifyOnly", "Tells you and downloads nothing."),
        })
        {
            var mark = requests.Mark();
            await Mode(page, name).ClickAsync();

            await Expect(Saved(page)).ToBeVisibleAsync();
            await Expect(Mode(page, name)).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(page.GetByText(summary, new() { Exact = true })).ToBeVisibleAsync();
            var sent = Assert.Single(requests.Since(mark));
            Assert.Equal(("PUT", UpdateSettings), (sent.Method, sent.PathAndQuery));
            var body = Sent(sent);
            Assert.Equal(value, (string?)body["mode"]);
            Assert.Equal(before["check_on_startup"]!.ToJsonString(), body["check_on_startup"]!.ToJsonString());
            Assert.Equal(before["check_interval_minutes"]!.ToJsonString(), body["check_interval_minutes"]!.ToJsonString());
            Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]));
            Assert.Equal(value, (string?)(await system.ReadAsync(UpdateSettings))["mode"]);
        }
    }

    [E2EFact]
    public async Task A_new_install_shows_only_the_choice_to_check_for_updates_and_switching_it_on_shows_the_rest()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), await E2EServer.StartWindowsInstallAsync(checksForUpdates: false), "About");
        var (page, requests) = (system.Page, system.Requests);
        await Expect(Checking(page, "Off")).ToBeCheckedAsync(new() { Timeout = SlowCheckMs });
        await Expect(page.GetByText("Weir looks for nothing until you switch this on.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("suite-settings-release-status")).ToContainTextAsync("Update checking is off, so Weir is not looking for new versions.");
        await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Update mode", Exact = true })).ToHaveCountAsync(0);
        await Expect(Every(page)).ToHaveCountAsync(0);
        Assert.False((bool)(await system.ReadAsync(UpdateSettings))["check_for_updates"]!);

        var mark = requests.Mark();
        await Checking(page, "On").ClickAsync();

        await Expect(Saved(page)).ToBeVisibleAsync();
        var sent = Sent(await requests.OnlyChangeSinceAsync(mark, "the choice to check for updates"));
        Assert.True((bool)sent["check_for_updates"]!);
        Assert.True((bool)(await system.ReadAsync(UpdateSettings))["check_for_updates"]!);
        Assert.Contains("\"checkForUpdates\": true", File.ReadAllText(Path.Join(system.Server.Home, "update-settings.json")), StringComparison.Ordinal);
        await Expect(Mode(page, "Auto")).ToBeVisibleAsync();
        await Expect(Every(page)).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Check_on_startup_and_Check_every_save_at_once_with_the_mode_the_server_holds()
    {
        await using var system = await OpenAsync();
        var (page, requests) = (system.Page, system.Requests);
        await Mode(page, "Download only").ClickAsync();
        await Expect(Saved(page)).ToBeVisibleAsync();
        var held = await system.ReadAsync(UpdateSettings);
        var wasOn = (bool)held["check_on_startup"]!;
        await Expect(Startup(page, wasOn ? "On" : "Off")).ToBeCheckedAsync();

        var mark = requests.Mark();
        await Startup(page, wasOn ? "Off" : "On").ClickAsync();

        await Expect(Startup(page, wasOn ? "Off" : "On")).ToBeCheckedAsync();
        var toggled = Sent(await requests.OnlyChangeSinceAsync(mark, "the startup check"));
        Assert.Equal(!wasOn, (bool)toggled["check_on_startup"]!);
        Assert.Equal("DownloadOnly", (string?)toggled["mode"]);
        await Expect(Saved(page)).ToBeVisibleAsync();
        Assert.Equal(!wasOn, (bool)(await system.ReadAsync(UpdateSettings))["check_on_startup"]!);

        await Expect(Every(page).Locator("option")).ToHaveTextAsync(["15 minutes", "30 minutes", "1 hour", "2 hours", "6 hours", "12 hours", "24 hours"]);
        mark = requests.Mark();
        await Every(page).SelectOptionAsync("120");

        await Expect(Saved(page)).ToBeVisibleAsync();
        var timed = Sent(await requests.OnlyChangeSinceAsync(mark, "the interval"));
        Assert.Equal(120, (int)timed["check_interval_minutes"]!);
        Assert.Equal("DownloadOnly", (string?)timed["mode"]);
        Assert.Equal(120, (int)(await system.ReadAsync(UpdateSettings))["check_interval_minutes"]!);
        await page.ReloadAsync();
        await Expect(Every(page)).ToHaveValueAsync("120", new() { Timeout = SlowCheckMs });
        await Expect(Mode(page, "Download only")).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [E2EFact]
    public async Task A_choice_the_server_cannot_take_says_so_and_the_page_shows_what_the_server_holds()
    {
        await using var system = await OpenAsync();
        var page = system.Page;
        var held = ModeWords[(string)(await system.ReadAsync(UpdateSettings))["mode"]!];
        var other = held == "Auto" ? "Notify only" : "Auto";
        await Expect(Mode(page, held)).ToHaveAttributeAsync("aria-pressed", "true");
        await system.Server.StopAsync();

        await Mode(page, other).ClickAsync();

        await Expect(page.GetByTestId("suite-settings-upgrade-tab").GetByRole(AriaRole.Alert).Filter(new() { HasText = "Can't reach Weir" })).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = 20_000 });
        await Expect(Mode(page, held)).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Saved(page)).ToHaveCountAsync(0);
    }
}
