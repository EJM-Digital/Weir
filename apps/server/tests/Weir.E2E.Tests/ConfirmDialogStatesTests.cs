using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The confirmation dialog in its other states, shown by System › Logs › Clear events: the confirm button stays off until RESET is typed, both
/// buttons and Escape do nothing while the request is under way, a refusal is said inside the dialog and leaves it open, and confirming
/// does what the dialog says and nothing else.
/// </summary>
public sealed class ConfirmDialogStatesTests(E2EServer server) : E2ETestBase(server)
{
    private const string SignInEvent = "auth.login_succeeded";
    private const string ResetPattern = "**/api/v1/suite/operational-history/reset";

    private async Task<(IPage Page, RequestLog Log)> OpenLogsAsync()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenLogsAsync(page);
        return (page, RequestLog.Watch(page));
    }

    private static ILocator Dialog(IPage page) => page.GetByTestId("logs-clear-dialog");

    private static async Task OpenDialogAsync(IPage page)
    {
        await page.GetByTestId("logs-clear-events").ClickAsync();
        await Expect(Dialog(page)).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task The_dialog_names_what_it_removes_and_the_button_works_only_once_RESET_is_typed()
    {
        var (page, log) = await OpenLogsAsync();
        var mark = log.Mark();

        await OpenDialogAsync(page);

        var dialog = Dialog(page);
        await Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = "Clear events?", Exact = true })).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync("This permanently removes:");
        await Expect(dialog).ToContainTextAsync("This cannot be undone.");
        // Focus starts on the safe choice, so Enter on a freshly opened dialog keeps everything.
        await Expect(page.GetByTestId("logs-clear-dialog-cancel")).ToBeFocusedAsync();
        var confirm = page.GetByTestId("logs-clear-dialog-confirm");
        await Expect(confirm).ToBeDisabledAsync();
        var typed = dialog.GetByLabel("Type RESET to confirm");
        await typed.FillAsync("RESE");
        await Expect(confirm).ToBeDisabledAsync();
        await typed.FillAsync("  reset ");
        await Expect(confirm).ToBeEnabledAsync();
        await typed.FillAsync("RESETS");
        await Expect(confirm).ToBeDisabledAsync();
        await Axe.AssertNoViolationsAsync(page, "The clear events dialog");

        // Asking what would go is a question: nothing has been sent that changes anything.
        Assert.Empty(log.Since(mark));
    }

    [E2EFact]
    public async Task Keep_them_and_Escape_close_the_dialog_and_send_nothing()
    {
        var (page, log) = await OpenLogsAsync();
        var signIns = await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent);
        Assert.True(signIns > 0, "The test needs an event to protect.");
        await OpenDialogAsync(page);
        await Dialog(page).GetByLabel("Type RESET to confirm").FillAsync("RESET");
        var mark = log.Mark();

        await page.GetByTestId("logs-clear-dialog-cancel").ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("logs-clear-events")).ToBeFocusedAsync();

        await OpenDialogAsync(page);
        await Dialog(page).GetByLabel("Type RESET to confirm").FillAsync("RESET");
        await page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("logs-clear-events")).ToBeFocusedAsync();

        // What was typed does not wait for the next time: a dialog opened again asks afresh.
        await OpenDialogAsync(page);
        await Expect(page.GetByTestId("logs-clear-dialog-confirm")).ToBeDisabledAsync();
        await page.Keyboard.PressAsync("Escape");

        log.AssertNothingSentSince(mark);
        Assert.Equal(signIns, await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent));
    }

    [E2EFact]
    public async Task While_the_request_is_under_way_both_buttons_and_Escape_do_nothing_and_it_says_it_is_clearing()
    {
        var (page, log) = await OpenLogsAsync();
        var release = new TaskCompletionSource();
        await page.RouteAsync(ResetPattern, async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        await OpenDialogAsync(page);
        var dialog = Dialog(page);
        await dialog.GetByLabel("Type RESET to confirm").FillAsync("RESET");
        var mark = log.Mark();

        await page.GetByTestId("logs-clear-dialog-confirm").ClickAsync();

        var confirm = page.GetByTestId("logs-clear-dialog-confirm");
        var cancel = page.GetByTestId("logs-clear-dialog-cancel");
        await Expect(confirm).ToHaveTextAsync("Clearing…");
        await Expect(confirm).ToBeDisabledAsync();
        await Expect(cancel).ToBeDisabledAsync();
        await Expect(dialog.GetByLabel("Type RESET to confirm")).ToBeDisabledAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeVisibleAsync();

        release.SetResult();

        await Expect(dialog).ToHaveCountAsync(0);
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/suite/operational-history/reset", sent.PathAndQuery);
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.Equal("RESET", (string?)body["confirm"]);
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carries the page's CSRF token.");
    }

    [E2EFact]
    public async Task Confirming_removes_the_events_says_how_many_and_leaves_the_account_signed_in()
    {
        var (page, _) = await OpenLogsAsync();
        Assert.True(await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent) > 0, "The test needs an event to clear.");
        await OpenDialogAsync(page);
        await Dialog(page).GetByLabel("Type RESET to confirm").FillAsync("reset");

        await page.GetByTestId("logs-clear-dialog-confirm").ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Events cleared." })).ToContainTextAsync("No media file was touched.");
        Assert.Equal(0, await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent));
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));
    }

    [E2EFact]
    public async Task A_refusal_is_said_inside_the_dialog_which_stays_open_with_both_buttons_working_again()
    {
        var (page, _) = await OpenLogsAsync();
        var signIns = await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent);
        await page.RouteAsync(ResetPattern, async route =>
        {
            // The server's own refusal: a token it does not know.
            var body = JsonNode.Parse(route.Request.PostData!)!.AsObject();
            body["csrf_token"] = "not-the-token";
            await route.ContinueAsync(new() { PostData = Encoding.UTF8.GetBytes(body.ToJsonString()) });
        });
        await OpenDialogAsync(page);
        await Dialog(page).GetByLabel("Type RESET to confirm").FillAsync("RESET");

        await page.GetByTestId("logs-clear-dialog-confirm").ClickAsync();

        var alert = Dialog(page).GetByRole(AriaRole.Alert);
        await Expect(alert).ToHaveTextAsync("Your confirmation token expired. Refresh the page and try again.");
        await Expect(page.GetByTestId("logs-clear-dialog-confirm")).ToHaveTextAsync("Clear events");
        await Expect(page.GetByTestId("logs-clear-dialog-confirm")).ToBeEnabledAsync();
        await Expect(page.GetByTestId("logs-clear-dialog-cancel")).ToBeEnabledAsync();
        Assert.Equal(signIns, await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, SignInEvent));
        await Axe.AssertNoViolationsAsync(page, "The clear events dialog after a refusal");
    }
}
