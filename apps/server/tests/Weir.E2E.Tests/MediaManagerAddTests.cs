using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Connections › Media managers, Add: the choice of manager and what each does, what Add sends, and closing the panel without adding.</summary>
public sealed class MediaManagerAddTests(E2EServer server) : E2ETestBase(server)
{
    private const string Nickname = "E2E added";

    [E2EFact]
    public async Task The_panel_opens_on_the_choice_of_manager_with_Deluno_first_and_a_line_for_each_kind()
    {
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var panel = await MediaManagersPage.OpenAddAsync(page);
        var kind = panel.GetByLabel("Which media manager is it?");

        await Expect(kind).ToBeFocusedAsync();
        await Expect(kind).ToHaveValueAsync("deluno");
        Assert.Equal(["Radarr", "Sonarr", "Deluno", "Something else"], await kind.Locator("option").AllInnerTextsAsync());
        var lines = new Dictionary<string, string>
        {
            ["radarr"] = "Lets Weir check what Radarr is still importing, and download a film again.",
            ["sonarr"] = "Lets Weir check what Sonarr is still importing, and download an episode again.",
            ["deluno"] = "Hands a file to Weir to work on, and waits to be told it is ready.",
            ["native"] = "Anything else that can send Weir a message.",
        };
        var add = panel.GetByTestId("media-manager-save");
        foreach (var (value, line) in lines)
        {
            await kind.SelectOptionAsync(value);
            await Expect(panel).ToContainTextAsync(line);

            // A manager Weir calls needs an address; one that only sends to Weir does not.
            if (value == "native")
            {
                await Expect(add).ToBeEnabledAsync();
                await Expect(panel).ToContainTextAsync("Optional. Leave it blank if it only sends messages to Weir and Weir never calls it.");
            }
            else
            {
                await Expect(add).ToBeDisabledAsync();
            }
        }
    }

    [E2EFact]
    public async Task Add_sends_the_kind_address_key_and_nickname_with_the_manager_on_and_the_panel_closes_on_the_new_card()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);
        var panel = await MediaManagersPage.OpenAddAsync(page);
        long? id = null;
        try
        {
            await panel.GetByLabel("Which media manager is it?").SelectOptionAsync("sonarr");
            await panel.GetByTestId("media-manager-base-url").FillAsync(sonarr.BaseUrl);
            await panel.GetByTestId("media-manager-api-key").FillAsync(sonarr.ApiKey);
            await panel.GetByTestId("media-manager-nickname").FillAsync(Nickname);
            var mark = log.Mark();

            await panel.GetByTestId("media-manager-save").ClickAsync();

            await Expect(panel).ToHaveCountAsync(0);
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {MediaManagersPage.Connections}", $"{sent.Method} {sent.PathAndQuery}");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
            body.Remove("csrf_token");
            Assert.True(
                JsonNode.DeepEquals(
                    new JsonObject
                    {
                        ["kind"] = "sonarr",
                        ["base_url"] = sonarr.BaseUrl,
                        ["api_key"] = sonarr.ApiKey,
                        ["nickname"] = Nickname,
                        ["enabled"] = true,
                        ["downloaded_scan_enabled"] = false,
                    },
                    body),
                $"The page sent {body.ToJsonString()}");

            var card = MediaManagersPage.Card(page, $"Sonarr on 127.0.0.1 · {Nickname}");
            await Expect(card).ToBeVisibleAsync();
            var listed = (await admin.GetAsync(MediaManagersPage.Connections)).Elements.Select(row => row!.AsObject()).Single(row => (string?)row["nickname"] == Nickname);
            id = (long)listed["id"]!;
            Assert.Equal("sonarr", (string)listed["kind"]!);
            Assert.Equal(sonarr.BaseUrl, (string)listed["base_url"]!);
            Assert.True((bool)listed["enabled"]!);
            Assert.False((bool)listed["downloaded_scan_enabled"]!);

            // Weir offers to make the secret at once, and Not now leaves the card as it is.
            var prompt = page.GetByTestId("media-manager-new-secret-prompt");
            await Expect(prompt).ToContainTextAsync($"Create a secret for Sonarr on 127.0.0.1 · {Nickname} now?");
            await page.GetByTestId("media-manager-new-secret-dismiss").ClickAsync();
            await Expect(prompt).ToHaveCountAsync(0);
            Assert.False((bool)(await MediaManagersPage.ReadAsync(admin, id.Value))["webhook_secret_is_set"]!);
        }
        finally
        {
            if (id is not null)
            {
                await ManagerConnections.RemoveAsync(admin, id.Value);
            }
        }
    }

    [E2EFact]
    public async Task Closing_the_panel_with_the_cross_Escape_or_a_click_outside_drops_what_was_typed_and_adds_nothing()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);
        var before = await SetupSnapshot.TakeAsync(admin);
        var mark = log.Mark();

        foreach (var close in new Func<ILocator, Task>[]
                 {
                     panel => panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync(),
                     _ => page.Keyboard.PressAsync("Escape"),
                     _ => page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).First.ClickAsync(),
                 })
        {
            var panel = await MediaManagersPage.OpenAddAsync(page);
            await panel.GetByTestId("media-manager-base-url").FillAsync("http://typed-and-dropped.invalid:7878");
            await panel.GetByTestId("media-manager-api-key").FillAsync("typed-and-dropped");
            await close(panel);
            await Expect(panel).ToHaveCountAsync(0);

            // Reopened, the form is empty again: nothing typed was kept.
            var reopened = await MediaManagersPage.OpenAddAsync(page);
            await Expect(reopened.GetByTestId("media-manager-base-url")).ToHaveValueAsync(string.Empty);
            await Expect(reopened.GetByTestId("media-manager-api-key")).ToHaveValueAsync(string.Empty);
            await reopened.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Expect(reopened).ToHaveCountAsync(0);
        }

        log.AssertNothingSentSince(mark);
        Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
    }
}
