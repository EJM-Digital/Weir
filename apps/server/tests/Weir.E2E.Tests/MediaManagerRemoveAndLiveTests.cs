using System.Text.Json;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Connections › Media managers: removing a manager nothing depends on, and the screen when its live link drops and returns.</summary>
public sealed class MediaManagerRemoveAndLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const string Nickname = "E2E remove";
    private const string Title = $"Sonarr on 127.0.0.1 · {Nickname}";
    private const string DialogId = "media-manager-remove-confirm";

    // The most the page is given to notice the live connection drop or return; only a hung page reaches it.
    private const float ReconnectMs = 20_000;

    [E2EFact]
    public async Task Removing_a_manager_nothing_depends_on_says_Removing_with_both_buttons_waiting_then_the_dialog_closes_and_the_card_goes()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);
        var card = MediaManagersPage.Card(page, Title);
        await card.GetByTestId("media-manager-remove").ClickAsync();
        var dialog = page.GetByTestId(DialogId);
        await Expect(dialog).ToContainTextAsync($"Remove {Title}?");
        await Expect(page.GetByTestId($"{DialogId}-cancel")).ToBeFocusedAsync();
        await RecordDialogButtonsAsync(page);
        var mark = log.Mark();

        await page.GetByTestId($"{DialogId}-confirm").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(card).ToHaveCountAsync(0);
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal($"DELETE {MediaManagersPage.Connections}/{id}", $"{sent.Method} {sent.PathAndQuery}");
        Assert.DoesNotContain((await admin.GetAsync(MediaManagersPage.Connections)).Elements, row => (long)row!["id"]! == id);

        // While Weir worked, the button said so and neither button could be pressed again.
        var seen = await page.EvaluateAsync<JsonElement>("() => window.__removeButtons");
        var states = seen.EnumerateArray().Select(state => $"{state.GetProperty("text").GetString()}|{state.GetProperty("confirmWaits").GetBoolean()}|{state.GetProperty("cancelWaits").GetBoolean()}").ToList();
        Assert.Contains("Removing…|True|True", states);
    }

    [E2EFact]
    public async Task The_screen_says_when_its_live_link_drops_and_catches_up_with_a_manager_added_meanwhile_when_it_returns()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Navigation.OpenTabAsync(page, "Connections", "Media managers");
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var banner = page.GetByTestId("live-connection-banner");
        var card = MediaManagersPage.Card(page, Title);
        await Expect(card).ToHaveCountAsync(0);
        long? id = null;
        try
        {
            await page.Context.SetOfflineAsync(true);
            await Expect(banner).ToHaveTextAsync("Live updates paused: can't reach Weir. Reconnecting…", new() { Timeout = ReconnectMs });

            // Added while the page could not hear of it: the page does not show it yet.
            id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
            await Expect(card).ToHaveCountAsync(0);

            await page.Context.SetOfflineAsync(false);
            await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
            await Expect(card).ToBeVisibleAsync(new() { Timeout = ReconnectMs });
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await page.Context.SetOfflineAsync(false);
            if (id is not null)
            {
                await ManagerConnections.RemoveAsync(admin, id.Value);
            }
        }
    }

    /// <summary>Writes down, as the page changes them, the words on the dialog's Remove button and whether either button is waiting.</summary>
    private static async Task RecordDialogButtonsAsync(IPage page) =>
        await page.EvaluateAsync(
            """
            id => {
              const seen = (window.__removeButtons = []);
              const look = () => {
                const confirm = document.querySelector(`[data-testid="${id}-confirm"]`);
                const cancel = document.querySelector(`[data-testid="${id}-cancel"]`);
                if (!confirm || !cancel) return;
                const state = { text: confirm.textContent.trim(), confirmWaits: confirm.disabled, cancelWaits: cancel.disabled };
                const last = seen[seen.length - 1];
                if (!last || JSON.stringify(last) !== JSON.stringify(state)) seen.push(state);
              };
              new MutationObserver(look).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
              look();
            }
            """,
            DialogId);
}
