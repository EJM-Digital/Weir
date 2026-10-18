using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Pause control in every page's header: what its menu offers and how it opens and closes, each way of pausing with the request the page
/// sends and what the server holds afterwards, Resume, the "keep looking for new files" choice, and what the person is told when Weir refuses.
/// </summary>
public sealed class PauseMenuTests(E2EServer server) : E2ETestBase(server)
{
    private const string PausePath = "/api/v1/pause";
    private static readonly string[] Durations = ["30 minutes", "2 hours", "8 hours", "Until I resume"];

    private async Task<(IPage Page, RequestLog Log)> OpenDashboardAsync()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        return (page, log);
    }

    private static async Task<JsonElement> ServerPauseAsync(IPage page, string baseUrl)
    {
        await using var response = await page.Context.APIRequest.GetAsync($"{baseUrl}{PausePath}");
        Assert.True(response.Ok, $"Reading the pause answered {response.Status}.");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.Clone();
    }

    private static JsonElement Body(SentRequest sent) => JsonDocument.Parse(sent.Body!).RootElement;

    [E2EFact]
    public async Task The_menu_opens_with_what_it_offers_and_closes_on_Escape_a_click_outside_or_the_button_again_sending_nothing()
    {
        var (page, log) = await OpenDashboardAsync();
        var open = page.GetByTestId("pause-open");
        var menu = page.GetByTestId("pause-menu");
        var shown = await ServerPauseAsync(page, BaseUrl);
        var mark = log.Mark();

        await open.ClickAsync();

        await Expect(open).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(menu.GetByText("Pause for how long?", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(menu.GetByRole(AriaRole.Button)).ToHaveTextAsync(Durations);
        var scan = menu.GetByRole(AriaRole.Checkbox, new() { Name = "Keep looking for new files while paused" });
        await Expect(scan).ToBeCheckedAsync(new() { Checked = shown.GetProperty("scan_while_paused").GetBoolean() });
        // What happens to work already running is said, in the server's own words, not left to be assumed.
        await Expect(page.GetByTestId("pause-policy")).ToHaveTextAsync(shown.GetProperty("in_flight_policy").GetString()!);
        await Axe.AssertNoViolationsAsync(page, "The pause menu");

        await page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToHaveCountAsync(0);
        await Expect(open).ToHaveAttributeAsync("aria-expanded", "false");

        await open.ClickAsync();
        await Expect(menu).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).ClickAsync();
        await Expect(menu).ToHaveCountAsync(0);

        await open.ClickAsync();
        await Expect(menu).ToBeVisibleAsync();
        await open.ClickAsync();
        await Expect(menu).ToHaveCountAsync(0);

        log.AssertNothingSentSince(mark);
        Assert.False((await ServerPauseAsync(page, BaseUrl)).GetProperty("paused").GetBoolean());
    }

    [E2EFact]
    public async Task The_menu_works_from_the_keyboard_alone()
    {
        var (page, log) = await OpenDashboardAsync();
        var open = page.GetByTestId("pause-open");
        await open.FocusAsync();

        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByTestId("pause-menu")).ToBeVisibleAsync();
        // Tab walks the four durations in order, then the checkbox; Shift+Tab walks back.
        foreach (var duration in Durations)
        {
            await page.Keyboard.PressAsync("Tab");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = duration, Exact = true })).ToBeFocusedAsync();
        }

        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByTestId("pause-scan-while-paused")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Until I resume", Exact = true })).ToBeFocusedAsync();
        for (var back = 0; back < Durations.Length - 1; back++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
        }

        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "30 minutes", Exact = true })).ToBeFocusedAsync();

        var mark = log.Mark();
        await page.Keyboard.PressAsync("Enter");

        await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal(30, Body(sent).GetProperty("pause_for_minutes").GetInt32());
        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);
    }

    [E2EFact]
    public async Task Until_I_resume_pauses_with_no_end_menu_closes_and_it_is_announced()
    {
        var (page, log) = await OpenDashboardAsync();
        await page.GetByTestId("pause-open").ClickAsync();
        var mark = log.Mark();

        await page.GetByRole(AriaRole.Button, new() { Name = "Until I resume", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("pause-menu")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("pause-announcement")).ToHaveTextAsync("Paused until you resume it.");
        await Expect(page.GetByTestId("pause-badge")).ToContainTextAsync("Paused");
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("PUT", sent.Method);
        Assert.Equal(PausePath, sent.PathAndQuery);
        var body = Body(sent);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("csrf_token").GetString()), "The pause carries the page's CSRF token.");
        Assert.True(body.GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pause_for_minutes").ValueKind);
        var held = await ServerPauseAsync(page, BaseUrl);
        Assert.True(held.GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, held.GetProperty("paused_until").ValueKind);
        await Axe.AssertNoViolationsAsync(page, "The header while paused");

        await page.GetByTestId("pause-resume").ClickAsync();
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Each_timed_pause_sends_its_minutes_and_the_server_ends_it_that_long_from_now()
    {
        var (page, log) = await OpenDashboardAsync();
        foreach (var (label, minutes) in new[] { ("30 minutes", 30), ("2 hours", 120), ("8 hours", 480) })
        {
            await page.GetByTestId("pause-open").ClickAsync();
            var mark = log.Mark();

            await page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync();

            await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("pause-menu")).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("pause-announcement")).ToHaveTextAsync($"Paused for {label}.");
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal(minutes, Body(sent).GetProperty("pause_for_minutes").GetInt32());
            var held = await ServerPauseAsync(page, BaseUrl);
            Assert.True(held.GetProperty("paused").GetBoolean());
            var lifts = held.GetProperty("paused_until").GetDateTimeOffset();
            Assert.InRange((lifts - DateTimeOffset.UtcNow).TotalMinutes, minutes - 2, minutes + 1);

            await page.GetByTestId("pause-resume").ClickAsync();
            await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        }
    }

    [E2EFact]
    public async Task Resume_says_it_is_resuming_while_it_saves_then_sends_paused_false_and_puts_the_menu_back()
    {
        var (page, log) = await OpenDashboardAsync();
        var scanBefore = (await ServerPauseAsync(page, BaseUrl)).GetProperty("scan_while_paused").GetBoolean();
        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);
        var resume = page.GetByTestId("pause-resume");
        await Expect(resume).ToBeVisibleAsync();
        var release = new TaskCompletionSource();
        await page.RouteAsync($"**{PausePath}", async route =>
        {
            if (route.Request.Method == "PUT")
            {
                await release.Task;
            }

            await route.ContinueAsync();
        });
        var mark = log.Mark();

        await resume.ClickAsync();

        await Expect(resume).ToHaveTextAsync("Resuming…");
        await Expect(resume).ToBeDisabledAsync();
        Assert.True((await ServerPauseAsync(page, BaseUrl)).GetProperty("paused").GetBoolean());
        release.SetResult();
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("pause-badge")).ToHaveCountAsync(0);
        var sent = Assert.Single(log.Since(mark));
        var body = Body(sent);
        Assert.False(body.GetProperty("paused").GetBoolean());
        Assert.Equal(scanBefore, body.GetProperty("scan_while_paused").GetBoolean());
        Assert.False((await ServerPauseAsync(page, BaseUrl)).GetProperty("paused").GetBoolean());
    }

    [E2EFact]
    public async Task Keep_looking_for_new_files_is_saved_as_chosen_and_carried_into_the_pause_and_the_resume()
    {
        var (page, log) = await OpenDashboardAsync();
        var onBefore = (await ServerPauseAsync(page, BaseUrl)).GetProperty("scan_while_paused").GetBoolean();
        var scan = page.GetByTestId("pause-scan-while-paused");
        await page.GetByTestId("pause-open").ClickAsync();
        await Expect(scan).ToBeCheckedAsync(new() { Checked = onBefore });

        // Turning it the other way saves it at once, with the pause as it is, and keeps the menu open.
        var mark = log.Mark();
        await scan.ClickAsync();
        await Expect(scan).ToBeCheckedAsync(new() { Checked = !onBefore });
        await Expect(page.GetByTestId("pause-menu")).ToBeVisibleAsync();
        var saved = await SingleSentAsync(log, mark);
        Assert.False(Body(saved).GetProperty("paused").GetBoolean());
        Assert.Equal(!onBefore, Body(saved).GetProperty("scan_while_paused").GetBoolean());
        Assert.Equal(!onBefore, (await ServerPauseAsync(page, BaseUrl)).GetProperty("scan_while_paused").GetBoolean());

        // The choice goes with the pause and with the resume.
        mark = log.Mark();
        await page.GetByRole(AriaRole.Button, new() { Name = "Until I resume", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("pause-resume")).ToBeVisibleAsync();
        Assert.Equal(!onBefore, Body(await SingleSentAsync(log, mark)).GetProperty("scan_while_paused").GetBoolean());
        mark = log.Mark();
        await page.GetByTestId("pause-resume").ClickAsync();
        await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync();
        Assert.Equal(!onBefore, Body(await SingleSentAsync(log, mark)).GetProperty("scan_while_paused").GetBoolean());

        await page.GetByTestId("pause-open").ClickAsync();
        await scan.ClickAsync();
        await Expect(scan).ToBeCheckedAsync(new() { Checked = onBefore });
        Assert.Equal(onBefore, (await ServerPauseAsync(page, BaseUrl)).GetProperty("scan_while_paused").GetBoolean());
    }

    /// <summary>The one change the page sent since the mark, once it has: the request follows the click a round trip later.</summary>
    private static async Task<SentRequest> SingleSentAsync(RequestLog log, int mark)
    {
        await Poll.UntilAsync(() => Task.FromResult(log.Since(mark).Count > 0), "the page to send its change");
        return Assert.Single(log.Since(mark));
    }

    [E2EFact]
    public async Task A_pause_the_server_refuses_says_so_leaves_the_menu_open_and_nothing_paused()
    {
        var (page, _) = await OpenDashboardAsync();
        await page.RouteAsync($"**{PausePath}", async route =>
        {
            if (route.Request.Method != "PUT")
            {
                await route.ContinueAsync();
                return;
            }

            // The server's own refusal: a token it does not know.
            var body = JsonNode.Parse(route.Request.PostData!)!.AsObject();
            body["csrf_token"] = "not-the-token";
            await route.ContinueAsync(new() { PostData = Encoding.UTF8.GetBytes(body.ToJsonString()) });
        });
        await page.GetByTestId("pause-open").ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "2 hours", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("pause-alert")).ToHaveTextAsync("Weir couldn't pause. Try again.");
        await Expect(page.GetByTestId("pause-menu")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("pause-resume")).ToHaveCountAsync(0);
        Assert.False((await ServerPauseAsync(page, BaseUrl)).GetProperty("paused").GetBoolean());
        await Axe.AssertNoViolationsAsync(page, "The pause menu after a refused pause");
    }

    [E2EFact]
    public async Task The_control_is_in_the_header_of_every_screen_and_its_menu_stays_on_the_screen_from_a_phone_to_a_laptop()
    {
        var page = await NewPageAsync(new ViewportSize { Width = 390, Height = 844 });
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        foreach (var address in new[] { "/", "/activity", "/library", "/setup/workflows", "/system" })
        {
            await page.GotoAsync($"{BaseUrl}{address}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByTestId("pause-open")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        }

        foreach (var width in new[] { 390, 600, 768, 920, 921, 1280 })
        {
            await page.SetViewportSizeAsync(width, 844);
            await page.GetByTestId("pause-open").ClickAsync();
            var menu = page.GetByTestId("pause-menu");
            await Expect(menu).ToBeVisibleAsync();
            var edges = await menu.EvaluateAsync<double[]>("element => { const box = element.getBoundingClientRect(); return [box.left, box.right, document.documentElement.clientWidth]; }");
            Assert.True(edges[0] >= 0 && edges[1] <= edges[2], $"At {width} wide the pause menu runs from {edges[0]} to {edges[1]}, past the edge of the screen.");
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToHaveCountAsync(0);
        }
    }
}
