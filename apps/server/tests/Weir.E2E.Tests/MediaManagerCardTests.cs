using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Connections › Media managers, a connection's card: Test, its colours, and the switch that turns it on and off.</summary>
public sealed class MediaManagerCardTests(E2EServer server) : E2ETestBase(server)
{
    private const string Nickname = "E2E card";
    private const string Title = $"Sonarr on 127.0.0.1 · {Nickname}";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    [E2EFact]
    public async Task Test_asks_that_manager_shows_Testing_while_it_waits_and_then_says_Answering_or_Not_answering_with_the_reason()
    {
        using var released = new ManualResetEventSlim(initialState: true);
        using var sonarr = FakeManager.StartArr("sonarr");
        sonarr.Route("GET", "/api/v3/system/status", _ =>
        {
            released.Wait(HangGuard);
            return Reply.Ok(new JsonObject { ["appName"] = "Sonarr", ["version"] = "4.0.0.0" });
        });
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, Nickname);
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var card = MediaManagersPage.Card(page, Title);
            var test = card.GetByTestId("media-manager-test");
            var status = card.GetByTestId("media-manager-status");

            // While the manager takes its time, the card says it is testing and the other buttons wait.
            released.Reset();
            var mark = log.Mark();
            await test.ClickAsync();
            await Expect(test).ToHaveTextAsync("Testing…");
            await Expect(test).ToBeDisabledAsync();
            await Expect(card.GetByTestId("media-manager-edit")).ToBeDisabledAsync();
            await Expect(card.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true })).ToBeDisabledAsync();
            await Expect(card.GetByTestId("media-manager-remove")).ToBeDisabledAsync();
            released.Set();

            await Expect(status.GetByText("Answering", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(status.Locator("[data-status=done]").First).ToHaveTextAsync("Answering");
            await Expect(status).Not.ToContainTextAsync("Last checked: never");
            await Expect(test).ToHaveTextAsync("Test");
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {MediaManagersPage.Connections}/{id}/test", $"{sent.Method} {sent.PathAndQuery}");
            Assert.True((bool)(await MediaManagersPage.ReadAsync(admin, id))["last_test_ok"]!);

            // A manager that answers with an error is Not answering, in the colour for broken, with the reason beneath.
            sonarr.Route("GET", "/api/v3/system/status", new JsonObject(), status: 500);
            await test.ClickAsync();
            await Expect(status.Locator("[data-status=broken]").First).ToHaveTextAsync("Not answering");
            await Expect(status.Locator(".mm-conn-row__problem")).ToBeVisibleAsync();
            Assert.Equal((string)(await MediaManagersPage.ReadAsync(admin, id))["last_test_detail"]!, await status.Locator(".mm-conn-row__problem").InnerTextAsync());
            Assert.False((bool)(await MediaManagersPage.ReadAsync(admin, id))["last_test_ok"]!);
        }
        finally
        {
            released.Set();
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    [E2EFact]
    public async Task Disable_and_Enable_send_the_opposite_of_what_is_on_say_Disabling_and_Enabling_while_they_work_and_the_chip_reads_Off()
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
            var toggle = card.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Disable|Enable|Disabling…|Enabling…)$") });
            await RecordTextAsync(toggle);
            await Expect(toggle).ToHaveTextAsync("Disable");
            var mark = log.Mark();

            await toggle.ClickAsync();
            await Expect(toggle).ToHaveTextAsync("Enable");
            await Expect(card.GetByTestId("media-manager-status").Locator("[data-status=idle]").First).ToHaveTextAsync("Off");
            var off = Assert.Single(log.Since(mark));
            Assert.Equal($"PUT {MediaManagersPage.Connections}/{id}", $"{off.Method} {off.PathAndQuery}");
            Assert.False((bool)JsonNode.Parse(off.Body!)!["enabled"]!);
            Assert.False((bool)(await MediaManagersPage.ReadAsync(admin, id))["enabled"]!);

            mark = log.Mark();
            await toggle.ClickAsync();
            await Expect(toggle).ToHaveTextAsync("Disable");
            var on = Assert.Single(log.Since(mark));
            Assert.True((bool)JsonNode.Parse(on.Body!)!["enabled"]!);
            Assert.True((bool)(await MediaManagersPage.ReadAsync(admin, id))["enabled"]!);

            var words = await page.EvaluateAsync<string[]>("() => window.__toggleTexts");
            Assert.Equal(["Disable", "Disabling…", "Enable", "Enabling…", "Disable"], words);
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }

    /// <summary>Writes down each text the element shows, as the page changes it, so a word that lasts a moment is not missed.</summary>
    private static async Task RecordTextAsync(ILocator element) =>
        await element.EvaluateAsync(
            """
            button => {
              const seen = (window.__toggleTexts = []);
              const look = () => {
                const text = button.textContent.trim();
                if (seen[seen.length - 1] !== text) seen.push(text);
              };
              new MutationObserver(look).observe(button, { subtree: true, childList: true, characterData: true });
              look();
            }
            """);
}
