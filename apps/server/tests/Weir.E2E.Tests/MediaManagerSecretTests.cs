using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup › Connections › Media managers, "How to point it at Weir": the address and instructions for Sonarr, Radarr and Deluno, the
/// secret made once and shown once, and what the wording says about whether a secret exists.
/// </summary>
public sealed class MediaManagerSecretTests(E2EServer server) : E2ETestBase(server)
{
    private const string Nickname = "E2E secret";

    [E2EFact]
    public async Task Sonarr_is_told_where_the_mapping_lives_and_how_to_add_the_import_webhook_with_its_secret_header_and_Deluno_is_not()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var deluno = FakeManager.StartDeluno();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var sonarrId = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, "E2E arr");
        var delunoId = await MediaManagersPage.ConnectAsync(admin, "deluno", deluno.BaseUrl, deluno.ApiKey, "E2E deluno");
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var arr = MediaManagersPage.Card(page, "Sonarr on 127.0.0.1 · E2E arr");
            var hand = MediaManagersPage.Card(page, "Deluno on 127.0.0.1 · E2E deluno");
            await arr.GetByText("How to point Sonarr on 127.0.0.1 · E2E arr at Weir").ClickAsync();
            await hand.GetByText("How to point Deluno on 127.0.0.1 · E2E deluno at Weir").ClickAsync();

            var pointer = arr.GetByTestId("media-manager-mapping-pointer");
            await Expect(pointer).ToContainTextAsync("picks up what Weir cleans through a remote path mapping from a workflow's watched folder to its output folder");
            var webhook = arr.GetByTestId("media-manager-import-webhook");
            await Expect(webhook).ToContainTextAsync("open Settings → Connect, add a Webhook, tick On Import and On Upgrade, and use the address below with method POST. Add a header named X-Webhook-Secret holding the secret.");
            await Expect(arr.GetByTestId("media-manager-webhook-url")).ToHaveTextAsync($"{BaseUrl}{(string)(await MediaManagersPage.ReadAsync(admin, sonarrId))["webhook_url_path"]!}");
            await Expect(hand.GetByTestId("media-manager-mapping-pointer")).ToHaveCountAsync(0);
            await Expect(hand.GetByTestId("media-manager-import-webhook")).ToHaveCountAsync(0);
            await Expect(hand.GetByTestId("media-manager-webhook-url")).ToBeVisibleAsync();
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, sonarrId);
            await ManagerConnections.RemoveAsync(admin, delunoId);
        }
    }

    [E2EFact]
    public async Task A_secret_is_made_once_shown_once_and_found_nowhere_afterwards_and_replacing_it_asks_first()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        var title = $"Sonarr on 127.0.0.1 · {Nickname}";
        var page = await NewPageAsync();
        var console = new List<string>();
        page.Console += (_, message) => console.Add(message.Text);
        try
        {
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var card = MediaManagersPage.Card(page, title);
            var banner = page.GetByTestId("media-manager-unsigned-webhook-warning");
            await Expect(banner).ToContainTextAsync($"{title} accepts webhooks without a secret. Create one under \"How to point it at Weir\", then add it to {title}.");
            await Expect(card.GetByText("Needs a secret", new() { Exact = true })).ToBeVisibleAsync();
            await card.GetByText($"How to point {title} at Weir").ClickAsync();
            await Expect(card).ToContainTextAsync($"There is no secret yet, so anything on your network could send files here pretending to be {title}.");

            // Making the first secret asks nothing and shows it once, with no way to copy it from the page.
            await card.GetByTestId("media-manager-generate-secret").ClickAsync();
            var shown = card.GetByTestId("media-manager-secret");
            await Expect(shown).ToContainTextAsync($"Copy this into {title} now — Weir will not show it again.");
            var secret = await shown.Locator("code").InnerTextAsync();
            Assert.Matches(new Regex(@"^\S{20,}$"), secret);
            await Expect(card).ToContainTextAsync($"{title} must send its secret with every file. Anything without it is ignored.");
            await Expect(card.GetByTestId("media-manager-generate-secret")).ToHaveTextAsync("Replace the secret");
            await Expect(banner).ToHaveCountAsync(0);
            await Expect(card.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("copy", RegexOptions.IgnoreCase) })).ToHaveCountAsync(0);
            Assert.True((bool)(await MediaManagersPage.ReadAsync(admin, id))["webhook_secret_is_set"]!);
            Assert.DoesNotContain(secret, (await admin.GetAsync(MediaManagersPage.Connections)).ToString(), StringComparison.Ordinal);

            // The page keeps it nowhere: not in an attribute, the address, the stored data, a cookie the page can read or the console.
            Assert.False(await HoldsAsync(page, secret), "The secret is held somewhere other than the one place it is shown.");
            Assert.DoesNotContain(console, line => line.Contains(secret, StringComparison.Ordinal));

            // After a reload it is gone from the whole page, and the card says a secret exists.
            await page.ReloadAsync();
            await Expect(MediaManagersPage.Card(page, title).GetByTestId("media-manager-secret")).ToHaveCountAsync(0);
            Assert.DoesNotContain(secret, await page.ContentAsync(), StringComparison.Ordinal);
            Assert.False(await HoldsAsync(page, secret));

            // Replacing it stops the manager reaching Weir until the new one is pasted in, so Weir asks; turning it down sends nothing.
            card = MediaManagersPage.Card(page, title);
            await card.GetByText($"How to point {title} at Weir").ClickAsync();
            var mark = log.Mark();
            await card.GetByTestId("media-manager-generate-secret").ClickAsync();
            var confirm = page.GetByTestId("media-manager-replace-secret-confirm");
            await Expect(confirm).ToContainTextAsync($"Replace {title}'s secret?");
            await Expect(confirm).ToContainTextAsync($"Replacing the secret stops {title} reaching Weir until you paste the new one into it.");
            await page.GetByTestId("media-manager-replace-secret-confirm-cancel").ClickAsync();
            await Expect(confirm).ToHaveCountAsync(0);
            log.AssertNothingSentSince(mark);

            await card.GetByTestId("media-manager-generate-secret").ClickAsync();
            await page.GetByTestId("media-manager-replace-secret-confirm-confirm").ClickAsync();
            await Expect(confirm).ToHaveCountAsync(0);
            var replacement = await card.GetByTestId("media-manager-secret").Locator("code").InnerTextAsync();
            Assert.NotEqual(secret, replacement);
            Assert.Single(log.Since(mark));
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    /// <summary>Whether the secret is in an attribute of the page, its address, its stored data or the cookies it can read.</summary>
    private static Task<bool> HoldsAsync(IPage page, string secret) =>
        page.EvaluateAsync<bool>(
            """
            secret => {
              const inAttribute = [...document.querySelectorAll('*')].some(element =>
                [...element.attributes].some(attribute => attribute.value.includes(secret)));
              const stored = [localStorage, sessionStorage].some(store =>
                Object.keys(store).some(key => key.includes(secret) || (store.getItem(key) ?? '').includes(secret)));
              return inAttribute || stored || location.href.includes(secret) || document.cookie.includes(secret);
            }
            """,
            secret);
}
