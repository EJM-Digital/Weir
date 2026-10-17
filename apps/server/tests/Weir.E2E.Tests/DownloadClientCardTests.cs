using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Connections › Download clients: the Add dialog and what it sends, a client's card (its status, Test, the switch), Edit, and the screen following the server.</summary>
public sealed class DownloadClientCardTests(E2EServer server) : E2ETestBase(server)
{
    private const string Route = $"{WeirClient.Api}/download-clients/connections";
    private const string Username = "weir-qbit";
    private const string Secret = "qbit-password";
    private const string Nickname = "E2E client";

    [E2EFact]
    public async Task Add_opens_a_dialog_on_the_kind_and_sends_the_kind_address_and_that_kinds_credentials_with_the_client_on()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var qbittorrent = FakeDownloadClient.Start("qbittorrent", Username, Secret);
        var page = await NewPageAsync();
        await OpenAsync(page);
        var log = RequestLog.Watch(page);
        long? id = null;
        try
        {
            await page.GetByTestId("download-client-add").ClickAsync();
            var panel = page.GetByTestId("download-client-add-panel");
            await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Add a download client", Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Add a download client" })).ToBeVisibleAsync();
            await Expect(panel.GetByTestId("download-client-kind")).ToBeFocusedAsync();

            await panel.GetByTestId("download-client-kind").SelectOptionAsync("qbittorrent");
            await panel.GetByTestId("download-client-base-url").FillAsync(qbittorrent.BaseUrl);
            await panel.GetByTestId("download-client-username").FillAsync(Username);
            await panel.GetByTestId("download-client-password").FillAsync(Secret);
            await panel.GetByTestId("download-client-nickname").FillAsync(Nickname);
            var mark = log.Mark();
            await panel.GetByTestId("download-client-save").ClickAsync();

            await Expect(panel).ToHaveCountAsync(0);
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {Route}", $"{sent.Method} {sent.PathAndQuery}");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
            body.Remove("csrf_token");
            Assert.True(
                JsonNode.DeepEquals(
                    new JsonObject
                    {
                        ["kind"] = "qbittorrent",
                        ["base_url"] = qbittorrent.BaseUrl,
                        ["username"] = Username,
                        ["password"] = Secret,
                        ["api_key"] = string.Empty,
                        ["nickname"] = Nickname,
                        ["enabled"] = true,
                    },
                    body),
                $"The page sent {body.ToJsonString()}");

            var row = (await admin.GetAsync(Route)).Elements.Select(item => item!.AsObject()).Single(item => (string?)item["nickname"] == Nickname);
            id = (long)row["id"]!;
            await Expect(page.GetByTestId("download-client-created-note")).ToHaveTextAsync(
                $"{(string)row["name"]!} · {Nickname} is connected. Weir will offer its folder as a suggestion in the workflow editor — nothing is applied on its own.");
            await Expect(page.GetByTestId("suite-settings-download-clients")).ToContainTextAsync("1 download client. Weir only reads their settings, and never changes them.");
        }
        finally
        {
            if (id is not null)
            {
                await admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
            }
        }
    }

    [E2EFact]
    public async Task A_client_is_Not_tested_yet_until_tested_then_Answering_with_a_time_or_Not_answering_with_the_reason_and_Off_when_switched_off()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var qbittorrent = FakeDownloadClient.Start("qbittorrent", Username, Secret);
        var id = await CreateAsync(admin, qbittorrent);
        try
        {
            var page = await NewPageAsync();
            await OpenAsync(page);
            var log = RequestLog.Watch(page);
            var card = CardOf(page);
            var status = card.GetByTestId("download-client-status");
            var test = card.GetByTestId("download-client-test");
            var toggle = card.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Disable|Enable|Disabling…|Enabling…)$") });
            await RecordTextsAsync(test, "__testTexts");
            await RecordTextsAsync(toggle, "__toggleTexts");

            await Expect(status.Locator("[data-status=idle]").First).ToHaveTextAsync("Not tested yet");
            await Expect(status).ToContainTextAsync("Last checked: never");
            await Expect(status).Not.ToContainTextAsync("Answering");

            var mark = log.Mark();
            await test.ClickAsync();
            await Expect(status.Locator("[data-status=done]").First).ToHaveTextAsync("Answering");
            await Expect(status).Not.ToContainTextAsync("Last checked: never");
            var tested = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {Route}/{id}/test", $"{tested.Method} {tested.PathAndQuery}");

            // A client that now turns the credentials away is Not answering, with the reason beneath.
            qbittorrent.Require(Username, "a-different-password");
            await test.ClickAsync();
            await Expect(status.Locator("[data-status=broken]").First).ToHaveTextAsync("Not answering");
            var detail = (string)(await ReadAsync(admin, id))["last_test_detail"]!;
            Assert.False(string.IsNullOrWhiteSpace(detail));
            await Expect(status.Locator(".mm-conn-row__problem")).ToHaveTextAsync(detail);

            // Switched off, the chip says Off; the switch sends the opposite of what is on.
            mark = log.Mark();
            await toggle.ClickAsync();
            await Expect(status.Locator("[data-status=idle]").First).ToHaveTextAsync("Off");
            await Expect(toggle).ToHaveTextAsync("Enable");
            var off = Assert.Single(log.Since(mark));
            Assert.Equal($"PUT {Route}/{id}", $"{off.Method} {off.PathAndQuery}");
            Assert.False((bool)JsonNode.Parse(off.Body!)!["enabled"]!);
            mark = log.Mark();
            await toggle.ClickAsync();
            await Expect(toggle).ToHaveTextAsync("Disable");
            Assert.True((bool)JsonNode.Parse(Assert.Single(log.Since(mark)).Body!)!["enabled"]!);
            Assert.True((bool)(await ReadAsync(admin, id))["enabled"]!);

            Assert.Equal(["Test", "Testing…", "Test", "Testing…", "Test"], await TextsAsync(page, "__testTexts"));
            Assert.Equal(["Disable", "Disabling…", "Enable", "Enabling…", "Disable"], await TextsAsync(page, "__toggleTexts"));
        }
        finally
        {
            await admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
        }
    }

    [E2EFact]
    public async Task Edit_sends_the_address_and_only_the_credentials_that_were_typed_or_changed()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var qbittorrent = FakeDownloadClient.Start("qbittorrent", Username, Secret);
        using var sabnzbd = FakeDownloadClient.Start("sabnzbd", username: null, secret: "sab-key");
        var id = await CreateAsync(admin, qbittorrent);
        var sabId = (long)(await admin.PostWithCsrfAsync(Route, new JsonObject { ["kind"] = "sabnzbd", ["base_url"] = sabnzbd.BaseUrl, ["api_key"] = "sab-key", ["nickname"] = "E2E sab", ["enabled"] = true })).Fields["id"]!;
        try
        {
            var page = await NewPageAsync();
            await OpenAsync(page);
            var log = RequestLog.Watch(page);
            var card = CardOf(page);

            // Only the address: no username, password or nickname go with it.
            await card.GetByTestId("download-client-edit").ClickAsync();
            var form = card.GetByTestId("download-client-edit-form");
            await Expect(form.GetByTestId("download-client-edit-username")).ToHaveValueAsync(Username);
            await Expect(form.GetByTestId("download-client-edit-password")).ToHaveValueAsync(string.Empty);
            await Expect(card.GetByTestId("download-client-test")).ToBeDisabledAsync();
            await form.GetByTestId("download-client-edit-base-url").FillAsync(qbittorrent.BaseUrl + "/");
            var mark = log.Mark();
            await form.GetByTestId("download-client-edit-save").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), id, new JsonObject { ["base_url"] = qbittorrent.BaseUrl + "/" });

            // A changed username, a typed password and a cleared nickname go; a nickname left alone does not.
            card = CardOf(page);
            await card.GetByTestId("download-client-edit").ClickAsync();
            form = card.GetByTestId("download-client-edit-form");
            await form.GetByTestId("download-client-edit-username").FillAsync("someone-else");
            await form.GetByTestId("download-client-edit-password").FillAsync("typed-password");
            await form.GetByTestId("download-client-edit-nickname").FillAsync(string.Empty);
            mark = log.Mark();
            await form.GetByTestId("download-client-edit-save").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), id, new JsonObject { ["base_url"] = (string)(await ReadAsync(admin, id))["base_url"]!, ["username"] = "someone-else", ["password"] = "typed-password", ["nickname"] = string.Empty });
            var held = await ReadAsync(admin, id);
            Assert.Equal("someone-else", (string)held["username"]!);
            Assert.True((bool)held["password_is_saved"]!);

            // A client that signs in with a key sends it only when one is typed.
            var sab = Card(page, "E2E sab");
            await sab.GetByTestId("download-client-edit").ClickAsync();
            var sabForm = sab.GetByTestId("download-client-edit-form");
            await Expect(sabForm.GetByTestId("download-client-edit-api-key")).ToHaveValueAsync(string.Empty);
            await sabForm.GetByTestId("download-client-edit-api-key").FillAsync("a-new-key");
            mark = log.Mark();
            await sabForm.GetByTestId("download-client-edit-save").ClickAsync();
            await Expect(sabForm).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), sabId, new JsonObject { ["base_url"] = sabnzbd.BaseUrl, ["api_key"] = "a-new-key" });

            // Cancel with nothing changed closes at once; after a change it asks.
            sab = Card(page, "E2E sab");
            await sab.GetByTestId("download-client-edit").ClickAsync();
            await sab.GetByTestId("download-client-edit-cancel").ClickAsync();
            await Expect(page.GetByTestId("settings-unsaved-changes")).ToHaveCountAsync(0);
            await sab.GetByTestId("download-client-edit").ClickAsync();
            await sab.GetByTestId("download-client-edit-base-url").FillAsync("http://elsewhere.invalid:8080");
            await sab.GetByTestId("download-client-edit-cancel").ClickAsync();
            var dialog = page.GetByTestId("settings-unsaved-changes");
            await Expect(dialog).ToContainTextAsync($"You have unsaved changes to {(string)(await ReadAsync(admin, sabId))["name"]!} · E2E sab. Leave without saving?");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Leave without saving" }).ClickAsync();
            Assert.Equal(sabnzbd.BaseUrl, (string)(await ReadAsync(admin, sabId))["base_url"]!);
        }
        finally
        {
            await admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
            await admin.DeleteWithCsrfBodyAsync($"{Route}/{sabId}");
        }
    }

    [E2EFact]
    public async Task A_test_made_elsewhere_changes_the_clients_status_on_the_open_screen_without_a_reload()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var qbittorrent = FakeDownloadClient.Start("qbittorrent", Username, Secret);
        var id = await CreateAsync(admin, qbittorrent);
        try
        {
            var page = await NewPageAsync();
            await page.RunAndWaitForResponseAsync(
                () => Navigation.EnsureSignedInAsync(page, BaseUrl),
                response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
            await Navigation.OpenTabAsync(page, "Connections", "Download clients");
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            var status = CardOf(page).GetByTestId("download-client-status");
            await Expect(status).ToContainTextAsync("Not tested yet");

            var answered = await admin.PostWithCsrfAsync($"{Route}/{id}/test");
            Assert.True(answered.Status == HttpStatusCode.OK, answered.ToString());
            await Expect(status.Locator("[data-status=done]").First).ToHaveTextAsync("Answering");

            qbittorrent.Require(Username, "a-different-password");
            var refused = await admin.PostWithCsrfAsync($"{Route}/{id}/test");
            Assert.True(refused.Status == HttpStatusCode.OK, refused.ToString());
            await Expect(status.Locator("[data-status=broken]").First).ToHaveTextAsync("Not answering");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
        }
    }

    private static ILocator Card(IPage page, string nickname) => page.GetByTestId("download-client-card").Filter(new() { HasText = nickname });

    private static ILocator CardOf(IPage page) => Card(page, Nickname);

    private async Task OpenAsync(IPage page)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Download clients");
        await Expect(page.GetByTestId("suite-settings-download-clients-tab")).ToBeVisibleAsync();
    }

    private static async Task<long> CreateAsync(WeirClient admin, FakeDownloadClient client)
    {
        var created = await admin.PostWithCsrfAsync(Route, new JsonObject
        {
            ["kind"] = "qbittorrent",
            ["base_url"] = client.BaseUrl,
            ["username"] = Username,
            ["password"] = Secret,
            ["nickname"] = Nickname,
            ["enabled"] = true,
        });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    private static async Task<JsonObject> ReadAsync(WeirClient admin, long id) =>
        (await admin.GetAsync(Route)).Elements.Select(row => row!.AsObject()).Single(row => (long)row["id"]! == id);

    private static void AssertSent(IReadOnlyList<SentRequest> sent, long id, JsonObject expected)
    {
        var request = Assert.Single(sent);
        Assert.Equal($"PUT {Route}/{id}", $"{request.Method} {request.PathAndQuery}");
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
        body.Remove("csrf_token");
        Assert.True(JsonNode.DeepEquals(expected, body), $"The page sent {body.ToJsonString()}");
    }

    /// <summary>Writes down each word the button shows, as the page changes it, so a word that lasts a moment is not missed.</summary>
    private static async Task RecordTextsAsync(ILocator button, string name) =>
        await button.EvaluateAsync(
            """
            (element, name) => {
              const seen = (window[name] = []);
              const look = () => {
                const text = element.textContent.trim();
                if (seen[seen.length - 1] !== text) seen.push(text);
              };
              new MutationObserver(look).observe(element, { subtree: true, childList: true, characterData: true });
              look();
            }
            """,
            name);

    private static async Task<List<string>> TextsAsync(IPage page, string name) =>
        [.. (await page.EvaluateAsync<JsonElement>("name => window[name]", name)).EnumerateArray().Select(item => item.GetString()!)];
}
