using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Library setup, the panel that slides over the Library page, in a real browser: Save waits for a change, says it is saving while the
/// server works and closes the panel once it has saved, and leaving with an edit that was not saved always asks first, whichever way
/// the person leaves (Cancel, the cross, a click beside the panel, Escape), while leaving with none does not.
/// </summary>
public sealed class LibrarySetupPanelTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    [E2EFact]
    public async Task Save_is_off_until_something_changes_says_Saving_while_the_server_works_and_closes_the_panel_once_it_is_saved()
    {
        await using var rig = await StartRigAsync();
        var (page, log) = await OpenSetupAsync(rig);
        var panel = page.GetByTestId("library-setup-panel");
        var save = page.GetByTestId("library-setup-save");
        var cancel = panel.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true });
        await Expect(save).ToBeDisabledAsync();
        await Expect(save).ToHaveTextAsync("Save");

        await Profile(page).SelectOptionAsync(new SelectOptionValue { Index = 0 });

        await Expect(save).ToBeEnabledAsync();
        Assert.NotNull((await rig.SettingsAsync())["library_rule_set_id"]);
        var mark = log.Mark();
        using (rig.HoldWrites())
        {
            await save.ClickAsync();

            await Expect(save).ToHaveTextAsync("Saving…");
            await Expect(save).ToBeDisabledAsync();
            await Expect(cancel).ToBeDisabledAsync();
            await Expect(panel).ToBeVisibleAsync();
        }

        await Expect(panel).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("PUT", sent.Method);
        Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-settings", sent.PathAndQuery, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(sent.Body!)!["library_rule_set_id"]);
        Assert.Null((await rig.SettingsAsync())["library_rule_set_id"]);
    }

    [E2EFact]
    public async Task Leaving_with_no_change_closes_the_panel_by_every_way_without_asking()
    {
        await using var rig = await StartRigAsync();
        var (page, log) = await OpenSetupAsync(rig);
        var panel = page.GetByTestId("library-setup-panel");
        var mark = log.Mark();

        await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);

        await OpenAsync(page);
        await panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);

        await OpenAsync(page);
        await page.Locator(".mm-drawer-backdrop").ClickAsync(new() { Position = new() { X = 700, Y = 450 } });
        await Expect(panel).ToHaveCountAsync(0);

        await OpenAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await Expect(panel).ToHaveCountAsync(0);

        await Expect(page.GetByTestId("settings-unsaved-changes")).ToHaveCountAsync(0);
        log.AssertNothingSentSince(mark);
    }

    [E2EFact]
    public async Task Leaving_with_an_unsaved_edit_asks_by_every_way_and_Keep_editing_keeps_the_edit_and_Leave_without_saving_drops_it()
    {
        await using var rig = await StartRigAsync();
        var (page, log) = await OpenSetupAsync(rig);
        var panel = page.GetByTestId("library-setup-panel");
        var question = page.GetByTestId("settings-unsaved-changes");
        var keep = question.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true });
        await Profile(page).SelectOptionAsync(new SelectOptionValue { Index = 0 });
        await Expect(page.GetByTestId("library-setup-save")).ToBeEnabledAsync();
        var mark = log.Mark();

        await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await ExpectAskedAsync(question, "E2E library films");
        await keep.ClickAsync();
        await Expect(question).ToHaveCountAsync(0);

        await panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await ExpectAskedAsync(question, "E2E library films");
        await keep.ClickAsync();
        await Expect(question).ToHaveCountAsync(0);

        await page.Locator(".mm-drawer-backdrop").ClickAsync(new() { Position = new() { X = 700, Y = 450 } });
        await ExpectAskedAsync(question, "E2E library films");
        await keep.ClickAsync();
        await Expect(question).ToHaveCountAsync(0);

        await panel.FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await ExpectAskedAsync(question, "E2E library films");
        await page.Keyboard.PressAsync("Escape");
        await Expect(question).ToHaveCountAsync(0);

        await Expect(panel).ToBeVisibleAsync();
        Assert.Equal("", await Profile(page).InputValueAsync());
        await Expect(page.GetByTestId("library-setup-save")).ToBeEnabledAsync();
        log.AssertNothingSentSince(mark);

        await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await question.GetByRole(AriaRole.Button, new() { Name = "Leave without saving", Exact = true }).ClickAsync();

        await Expect(panel).ToHaveCountAsync(0);
        await Expect(question).ToHaveCountAsync(0);
        log.AssertNothingSentSince(mark);
        Assert.NotNull((await rig.SettingsAsync())["library_rule_set_id"]);

        await OpenAsync(page);

        Assert.NotEqual("", await Profile(page).InputValueAsync());
        await Expect(page.GetByTestId("library-setup-save")).ToBeDisabledAsync();
    }

    private static async Task ExpectAskedAsync(ILocator question, string workflow)
    {
        await Expect(question).ToBeVisibleAsync();
        await Expect(question).ToContainTextAsync($"You have unsaved changes to the setup of {workflow}. Leave without saving?");
    }

    private static ILocator Profile(IPage page) => page.GetByTestId("library-rules-profile");

    private static async Task OpenAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Library setup" }).ClickAsync();
        await Expect(page.GetByTestId("library-setup-panel")).ToBeVisibleAsync(new() { Timeout = ServerMs });
    }

    /// <summary>A workflow whose library is cleaned by a profile of its own, which the panel can change back to "Same as the workflow".</summary>
    private static async Task<LibraryRig> StartRigAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<(IPage Page, RequestLog Log)> OpenSetupAsync(LibraryRig rig)
    {
        var page = await NewPageAsync(Desktop);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await OpenAsync(page);
        return (page, log);
    }
}
