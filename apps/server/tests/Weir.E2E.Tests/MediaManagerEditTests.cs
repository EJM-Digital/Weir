using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Connections › Media managers, Edit: the form opens in place, the buttons beside it wait, Cancel asks when something changed, and Save sends only what changed.</summary>
public sealed class MediaManagerEditTests(E2EServer server) : E2ETestBase(server)
{
    private const string Nickname = "E2E edit";
    private const string Title = $"Sonarr on 127.0.0.1 · {Nickname}";

    [E2EFact]
    public async Task Edit_opens_in_place_with_the_address_a_blank_key_and_no_name_while_the_other_buttons_wait_and_Cancel_closes_it_at_once()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var card = MediaManagersPage.Card(page, Title);
            var mark = log.Mark();

            await card.GetByTestId("media-manager-edit").ClickAsync();
            var form = card.GetByTestId("media-manager-edit-form");
            await Expect(form.GetByTestId("media-manager-edit-base-url")).ToHaveValueAsync(sonarr.BaseUrl);
            await Expect(form.GetByTestId("media-manager-edit-api-key")).ToHaveValueAsync(string.Empty);
            await Expect(form.GetByLabel("Name", new() { Exact = true })).ToHaveCountAsync(0);
            await Expect(form.GetByTestId("media-manager-edit-downloaded-scan")).ToBeVisibleAsync();
            await Expect(card.GetByTestId("media-manager-test")).ToBeDisabledAsync();
            await Expect(card.GetByTestId("media-manager-edit")).ToBeDisabledAsync();
            await Expect(card.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true })).ToBeDisabledAsync();
            await Expect(card.GetByTestId("media-manager-remove")).ToBeDisabledAsync();

            await form.GetByTestId("media-manager-edit-cancel").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("settings-unsaved-changes")).ToHaveCountAsync(0);
            await Expect(card.GetByTestId("media-manager-edit")).ToBeEnabledAsync();
            log.AssertNothingSentSince(mark);
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    [E2EFact]
    public async Task Cancel_after_a_change_asks_first_and_Keep_editing_and_Escape_keep_the_edit_while_Leave_drops_it()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var card = MediaManagersPage.Card(page, Title);
            await card.GetByTestId("media-manager-edit").ClickAsync();
            var form = card.GetByTestId("media-manager-edit-form");
            var address = form.GetByTestId("media-manager-edit-base-url");
            var dialog = page.GetByTestId("settings-unsaved-changes");
            var mark = log.Mark();
            await address.FillAsync("http://changed.invalid:8989");

            await form.GetByTestId("media-manager-edit-cancel").ClickAsync();
            await Expect(dialog).ToContainTextAsync($"You have unsaved changes to {Title}. Leave without saving?");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" }).ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(address).ToHaveValueAsync("http://changed.invalid:8989");

            await form.GetByTestId("media-manager-edit-cancel").ClickAsync();
            await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(address).ToHaveValueAsync("http://changed.invalid:8989");

            await form.GetByTestId("media-manager-edit-cancel").ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Leave without saving" }).ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            log.AssertNothingSentSince(mark);
            Assert.Equal(sonarr.BaseUrl, (string)(await MediaManagersPage.ReadAsync(admin, id))["base_url"]!);
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    [E2EFact]
    public async Task Save_sends_the_address_and_only_what_else_was_changed_and_a_blank_nickname_clears_it()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var moved = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var card = MediaManagersPage.Card(page, Title);

            // Only the address changed: no key and no nickname go with it.
            await card.GetByTestId("media-manager-edit").ClickAsync();
            var form = card.GetByTestId("media-manager-edit-form");
            await form.GetByTestId("media-manager-edit-base-url").FillAsync(moved.BaseUrl);
            var mark = log.Mark();
            await form.GetByTestId("media-manager-edit-save").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), id, new JsonObject { ["base_url"] = moved.BaseUrl, ["downloaded_scan_enabled"] = false });
            var held = await MediaManagersPage.ReadAsync(admin, id);
            Assert.Equal(moved.BaseUrl, (string)held["base_url"]!);
            Assert.Equal(Nickname, (string)held["nickname"]!);

            // A typed key, a changed nickname and the Downloaded Scan box go with it; the nickname is trimmed.
            card = MediaManagersPage.Card(page, Title);
            await card.GetByTestId("media-manager-edit").ClickAsync();
            form = card.GetByTestId("media-manager-edit-form");
            await form.GetByTestId("media-manager-edit-api-key").FillAsync("a-new-key");
            await form.GetByLabel("Nickname (optional)").FillAsync("  E2E renamed  ");
            await form.GetByTestId("media-manager-edit-downloaded-scan").CheckAsync();
            mark = log.Mark();
            await form.GetByTestId("media-manager-edit-save").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), id, new JsonObject { ["base_url"] = moved.BaseUrl, ["api_key"] = "a-new-key", ["nickname"] = "E2E renamed", ["downloaded_scan_enabled"] = true });
            held = await MediaManagersPage.ReadAsync(admin, id);
            Assert.Equal("E2E renamed", (string)held["nickname"]!);
            Assert.True((bool)held["downloaded_scan_enabled"]!);

            // A blank nickname clears it.
            card = MediaManagersPage.Card(page, "Sonarr on 127.0.0.1 · E2E renamed");
            await card.GetByTestId("media-manager-edit").ClickAsync();
            form = card.GetByTestId("media-manager-edit-form");
            await form.GetByLabel("Nickname (optional)").FillAsync(string.Empty);
            mark = log.Mark();
            await form.GetByTestId("media-manager-edit-save").ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            AssertSent(log.Since(mark), id, new JsonObject { ["base_url"] = moved.BaseUrl, ["nickname"] = string.Empty, ["downloaded_scan_enabled"] = true });
            Assert.True(string.IsNullOrEmpty((string?)(await MediaManagersPage.ReadAsync(admin, id))["nickname"]));
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    private static void AssertSent(IReadOnlyList<SentRequest> sent, long id, JsonObject expected)
    {
        var request = Assert.Single(sent);
        Assert.Equal($"PUT {MediaManagersPage.Connections}/{id}", $"{request.Method} {request.PathAndQuery}");
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
        body.Remove("csrf_token");
        Assert.True(JsonNode.DeepEquals(expected, body), $"The page sent {body.ToJsonString()}");
    }
}
