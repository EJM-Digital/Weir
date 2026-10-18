using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs › Clear events removes the Activity events and finished jobs for good. The dialog says what it would remove, as the server counts it,
/// asks for RESET before it works, and every other way out of it leaves everything where it was. Each test has a server of its own.
/// </summary>
public sealed class SystemLogClearEventsTests(E2EServer server) : E2ETestBase(server)
{
    private const string Marker = "clear-marker";
    private const int Marked = 4;
    private const string Preview = "/api/v1/suite/operational-history/preview";
    private const string Reset = "/api/v1/suite/operational-history/reset";
    private const string Log = "/api/v1/system/log";
    private const float ServerStoppedMs = 20_000;

    private static ILocator Dialog(IPage page) => page.GetByTestId("logs-clear-dialog");

    private static ILocator ClearEvents(IPage page) => page.GetByTestId("logs-clear-events");

    private static ILocator Typed(IPage page) => Dialog(page).GetByLabel("Type RESET to confirm");

    private static ILocator Confirm(IPage page) => page.GetByTestId("logs-clear-dialog-confirm");

    private async Task<SystemSession> OpenMarkedLogAsync()
    {
        var system = await SystemSession.OpenAsync(await NewPageAsync(), "About");
        for (var number = 1; number <= Marked; number++)
        {
            E2EDatabase.InsertActivityEvent(system.Server.DatabasePath, "auth.password_changed", "auth", $"{Marker} {number}", $"{Marker} {number} detail");
        }

        await system.Page.GotoAsync(
            $"{system.Server.BaseUrl.GetLeftPart(UriPartial.Authority)}/system?tab=logs&source=event&q={Marker}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(system.Page.GetByTestId("log-summary")).ToHaveTextAsync($"{Marked} entries matching · live");
        return system;
    }

    /// <summary>Presses Clear events… and returns what the server said the dialog should list, which is the answer the dialog asked for.</summary>
    private static async Task<JsonObject> OpenDialogAsync(IPage page)
    {
        var answer = await page.RunAndWaitForResponseAsync(() => ClearEvents(page).ClickAsync(), response => response.Url.EndsWith(Preview, StringComparison.Ordinal));
        await Expect(Dialog(page)).ToBeVisibleAsync();
        return JsonNode.Parse(await answer.TextAsync())!.AsObject();
    }

    private static async Task<int> MarkedEventsAsync(SystemSession system) =>
        (int)(await system.ReadAsync($"{Log}?q={Marker}"))["total"]!;

    [E2EFact]
    public async Task The_dialog_lists_what_the_server_counts_and_only_RESET_in_any_case_lets_it_clear()
    {
        await using var system = await OpenMarkedLogAsync();
        var (page, requests) = (system.Page, system.Requests);

        var counted = await OpenDialogAsync(page);

        var events = (int)counted["activity_events_deleted"]!;
        var jobs = (int)counted["jobs_deleted"]!;
        Assert.True(events >= Marked, "The preview does not count the events the log holds.");
        await Expect(Dialog(page)).ToContainTextAsync("This permanently removes:");
        await Expect(Dialog(page).GetByRole(AriaRole.Listitem)).ToHaveTextAsync([$"{events:N0} Activity {(events == 1 ? "event" : "events")}", $"{jobs:N0} finished {(jobs == 1 ? "job" : "jobs")}"]);
        await Expect(Dialog(page)).ToContainTextAsync("Queued and running work is kept, and so are all settings and the server log. No media file is touched. This cannot be undone.");
        await Expect(Confirm(page)).ToBeDisabledAsync();
        foreach (var wrong in new[] { "RES", "RESET NOW", "REST", string.Empty })
        {
            await Typed(page).FillAsync(wrong);
            await Expect(Confirm(page)).ToBeDisabledAsync();
        }

        var mark = requests.Mark();
        await Typed(page).FillAsync(" reset ");
        await Expect(Confirm(page)).ToBeEnabledAsync();
        await Confirm(page).ClickAsync();

        var cleared = await Poll.UntilAsync(
            () => Task.FromResult(requests.Since(mark).SingleOrDefault()),
            "the page to ask for the events to be cleared");
        Assert.Equal(("POST", Reset), (cleared.Method, cleared.PathAndQuery));
        var sent = JsonNode.Parse(cleared.Body!)!.AsObject();
        Assert.Equal("reset", (string?)sent["confirm"]);
        Assert.False(string.IsNullOrEmpty((string?)sent["csrf_token"]));
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        var notice = page.GetByRole(AriaRole.Status).Filter(new() { HasTextRegex = new Regex("^Events cleared") });
        await Expect(notice).ToHaveTextAsync(new Regex(@"^Events cleared\. Removed [\d,]+ Activity events? and [\d,]+ finished jobs?\. No media file was touched\.$"));
        await Expect(page.GetByTestId("log-empty")).ToContainTextAsync("Nothing in the log matches these filters.");
        await Expect(page.GetByTestId("log-summary")).ToHaveTextAsync("0 entries matching · live");
        Assert.Equal(0, await MarkedEventsAsync(system));
    }

    [E2EFact]
    public async Task Escape_Keep_them_and_Enter_on_the_fresh_dialog_clear_nothing_and_a_click_outside_changes_nothing()
    {
        await using var system = await OpenMarkedLogAsync();
        var (page, requests) = (system.Page, system.Requests);
        // RESET is typed first wherever the way out does not need the focus where the dialog put it.
        var leaves = new (string Way, bool TypeFirst, Func<Task> Leave)[]
        {
            ("Escape", true, () => page.Keyboard.PressAsync("Escape")),
            ("Keep them", true, () => page.GetByRole(AriaRole.Button, new() { Name = "Keep them", Exact = true }).ClickAsync()),
            ("Enter, with the focus where the dialog put it", false, () => page.Keyboard.PressAsync("Enter")),
        };

        // The dimmed area around the card does nothing: the question stays until it is answered.
        await OpenDialogAsync(page);
        await Typed(page).FillAsync("RESET");
        var outsideMark = requests.Mark();
        await Dialogs.ClickOutsideAsync(Dialog(page));
        await Expect(Dialog(page)).ToBeVisibleAsync();
        await Expect(Typed(page)).ToHaveValueAsync("RESET");
        requests.AssertNothingSentSince(outsideMark);
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep them", Exact = true }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);

        foreach (var (way, typeFirst, leave) in leaves)
        {
            await OpenDialogAsync(page);
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Keep them", Exact = true })).ToBeFocusedAsync();
            if (typeFirst)
            {
                await Typed(page).FillAsync("RESET");
            }

            var mark = requests.Mark();

            await leave();

            try
            {
                await Expect(Dialog(page)).ToHaveCountAsync(0);
            }
            catch (PlaywrightException)
            {
                Assert.Fail($"The dialog stayed open after {way}.");
            }

            requests.AssertNothingSentSince(mark);
            Assert.True(Marked == await MarkedEventsAsync(system), $"Leaving the dialog by {way} cleared events.");
            await Expect(page.GetByTestId("log-summary")).ToHaveTextAsync($"{Marked} entries matching · live");
        }
    }

    [E2EFact]
    public async Task A_clear_the_server_cannot_do_says_so_in_the_dialog_and_leaves_it_open()
    {
        await using var system = await OpenMarkedLogAsync();
        var page = system.Page;
        await OpenDialogAsync(page);
        await Typed(page).FillAsync("RESET");
        await system.Server.StopAsync();

        await Confirm(page).ClickAsync();

        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = ServerStoppedMs });
        await Expect(Dialog(page)).ToBeVisibleAsync();
        await Expect(Confirm(page)).ToBeEnabledAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(page)).ToHaveCountAsync(0);
    }
}
