using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The daily clean removes tracks from every file that has them without asking each time, so switching it on in Library setup
/// shows a warning first. Cancel on that warning leaves it off: nothing is sent, Save stays disabled, and the server still says the
/// daily clean is off.
/// </summary>
public sealed class LibraryDailyCleanWarningTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;

    [E2EFact]
    public async Task Cancel_on_the_warning_leaves_the_daily_clean_off_with_Save_disabled_and_sends_nothing()
    {
        await using var rig = await LibraryRig.StartAsync();
        var (page, log) = await OpenLibrarySetupAsync(rig);
        var daily = DailyClean(page);
        await daily.GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true }).ClickAsync();
        var warning = page.GetByRole(AriaRole.Alertdialog, new() { Name = "Switch on the daily clean" });
        await Expect(warning).ToContainTextAsync("Removed tracks cannot be put back.");
        var mark = log.Mark();

        await warning.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        await Expect(warning).ToHaveCountAsync(0);
        await Expect(daily.GetByRole(AriaRole.Radio, new() { Name = "Off", Exact = true })).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(daily.GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true })).ToHaveAttributeAsync("aria-checked", "false");
        await Expect(page.GetByTestId("library-setup-save")).ToBeDisabledAsync();
        log.AssertNothingSentSince(mark);
        Assert.False((bool)(await rig.SettingsAsync())["library_schedule_enabled"]!);
    }

    [E2EFact]
    public async Task Switch_it_on_then_Save_turns_the_daily_clean_on_with_the_confirmation()
    {
        await using var rig = await LibraryRig.StartAsync();
        var (page, log) = await OpenLibrarySetupAsync(rig);
        var daily = DailyClean(page);
        await daily.GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true }).ClickAsync();
        var warning = page.GetByRole(AriaRole.Alertdialog, new() { Name = "Switch on the daily clean" });
        var mark = log.Mark();

        await warning.GetByRole(AriaRole.Button, new() { Name = "Switch it on", Exact = true }).ClickAsync();

        await Expect(warning).ToHaveCountAsync(0);
        log.AssertNothingSentSince(mark);
        Assert.False((bool)(await rig.SettingsAsync())["library_schedule_enabled"]!, "Nothing is switched on until Save.");
        await page.GetByTestId("library-setup-save").ClickAsync();

        await Expect(page.GetByTestId("library-setup-panel")).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-schedule", sent.PathAndQuery, StringComparison.Ordinal);
        var body = JsonNode.Parse(sent.Body!)!;
        Assert.True((bool)body["enabled"]!);
        Assert.True((bool)body["confirm_final_removal"]!);
        Assert.True((bool)(await rig.SettingsAsync())["library_schedule_enabled"]!);
    }

    private async Task<(IPage Page, RequestLog Log)> OpenLibrarySetupAsync(LibraryRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Library setup" }).ClickAsync();
        await Expect(page.GetByTestId("library-setup-panel")).ToBeVisibleAsync(new() { Timeout = ServerMs });
        return (page, log);
    }

    private static ILocator DailyClean(IPage page) =>
        page.GetByRole(AriaRole.Radiogroup, new() { NameRegex = new("Check and clean once a day") });
}
