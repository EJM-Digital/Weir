using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// While a dialog is open, everything outside it is out of reach: the side menu cannot be clicked, tabbed to or read, focus stays in the
/// dialog, and closing it frees the page and hands focus back to the button that opened it. The dialog under test is System › Backups'
/// restore question, which needs only one backup to reach.
/// </summary>
public sealed class DialogContainmentTests(E2EServer server) : E2ETestBase(server)
{
    private const int TabPresses = 12;

    [E2EFact]
    public async Task A_click_on_the_side_menu_while_a_dialog_is_open_goes_nowhere_and_the_dialog_stays()
    {
        var (page, _, dialog) = await OpenRestoreQuestionAsync();
        var activity = ActivityLink(page);

        await ClickMiddleOfAsync(page, activity);

        Assert.DoesNotContain("/activity", page.Url, StringComparison.Ordinal);
        await Expect(dialog).ToBeVisibleAsync();
        Assert.True(await IsInertAsync(activity), "The side menu must be inert while a dialog is open.");
    }

    [E2EFact]
    public async Task Tab_and_Shift_Tab_stay_inside_the_dialog_and_never_reach_the_side_menu_or_the_page_behind()
    {
        var (page, _, dialog) = await OpenRestoreQuestionAsync();

        foreach (var shift in new[] { false, true })
        {
            for (var press = 0; press < TabPresses; press++)
            {
                await page.Keyboard.PressAsync(shift ? "Shift+Tab" : "Tab");
                var inside = await page.EvaluateAsync<bool>("() => document.activeElement?.closest('[role=dialog]') !== null");
                Assert.True(inside, $"After {press + 1} {(shift ? "Shift+Tab" : "Tab")} presses focus left the dialog.");
            }
        }

        await Expect(dialog).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Cancel_hands_focus_back_to_the_button_that_opened_the_dialog_and_frees_the_side_menu()
    {
        var (page, restore, dialog) = await OpenRestoreQuestionAsync();

        await dialog.GetByTestId("restore-configuration-dialog-cancel").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(restore).ToBeFocusedAsync();
        Assert.False(await IsInertAsync(ActivityLink(page)), "The side menu must work again once the dialog is closed.");
        await ActivityLink(page).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/activity"), new() { Timeout = Navigation.UrlAssertMs });
    }

    [E2EFact]
    public async Task Escape_hands_focus_back_to_the_button_that_opened_the_dialog()
    {
        var (page, restore, dialog) = await OpenRestoreQuestionAsync();

        await page.Keyboard.PressAsync("Escape");

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(restore).ToBeFocusedAsync();
        Assert.False(await IsInertAsync(restore), "The button that opened the dialog must work again once it is closed.");
    }

    /// <summary>Signs in on System › Backups with one backup made, and opens the question asked before a backup is restored.</summary>
    private async Task<(IPage Page, ILocator Restore, ILocator Dialog)> OpenRestoreQuestionAsync()
    {
        using var client = new WeirClient(new Uri(BaseUrl));
        await client.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
        var made = await client.PostWithCsrfAsync($"{WeirClient.Api}/suite/configuration-backups");
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "System", "Backups");
        var restore = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Restore the backup taken") }).First;
        await restore.ClickAsync();
        var dialog = page.GetByTestId("restore-configuration-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        return (page, restore, dialog);
    }

    /// <summary>Found by its address, not its role: a screen reader skips what is inert, and so may the role search, which would hide the very thing this checks.</summary>
    private static ILocator ActivityLink(IPage page) => page.Locator("#mm-primary-sidebar a[href='/activity']");

    /// <summary>A click with the mouse where the element is, which reaches it only if nothing in front of it takes it away; Playwright's own click refuses to try.</summary>
    private static async Task ClickMiddleOfAsync(IPage page, ILocator element)
    {
        var box = await element.BoundingBoxAsync() ?? throw new InvalidOperationException("The element is not on screen.");
        await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }

    private static async Task<bool> IsInertAsync(ILocator element) => await element.EvaluateAsync<bool>("el => el.closest('[inert]') !== null");
}
