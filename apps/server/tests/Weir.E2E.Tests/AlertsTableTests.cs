using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Connections › Alerts, the table: what each row says about an alert, the words for the events it can hear, and the removal of one that is already gone.</summary>
public sealed class AlertsTableTests(E2EServer server) : E2ETestBase(server)
{
    private const string Route = $"{WeirClient.Api}/suite/notification-channels";
    private const string Token = "7d3f9a1c-secret-token-0042";
    private const string DialogId = "notification-channel-remove-confirm";

    [E2EFact]
    public async Task Each_alert_shows_its_name_provider_and_Disabled_chip_its_address_without_the_token_and_the_four_events_in_reading_order()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var webhook = await CreateAsync(admin, "E2E hook", "webhook", $"https://hooks.example.test/api/webhooks/123456/{Token}", enabled: true);
        var discord = await CreateAsync(admin, "E2E discord", "discord", $"https://discord.com/api/webhooks/987654/{Token}", enabled: false, events: ["job_failed", "processing_job_completed"]);
        try
        {
            var page = await NewPageAsync();
            await OpenAsync(page);
            var headings = page.Locator("table thead th");

            // The events read: a file finished, a file failed for good, anything failed for good, anything finished.
            Assert.Equal(
                ["Alert", "A file finished", "A file failed for good", "Anything failed for good", "Anything finished", "Actions"],
                (await headings.AllInnerTextsAsync()).Select(text => text.Trim().Replace("\n", " ", StringComparison.Ordinal)).Select(ToTitleCase).ToList());

            var hook = RowOf(page, "E2E hook");
            await Expect(hook.GetByRole(AriaRole.Rowheader)).ToContainTextAsync("webhook");
            await Expect(hook.GetByRole(AriaRole.Rowheader)).ToContainTextAsync("https://hooks.example.test/api/webhooks/123456/••••");
            await Expect(hook.GetByRole(AriaRole.Rowheader)).Not.ToContainTextAsync("Disabled");
            var off = RowOf(page, "E2E discord");
            await Expect(off.GetByRole(AriaRole.Rowheader)).ToContainTextAsync("discord");
            await Expect(off.GetByRole(AriaRole.Rowheader).Locator(".mm-chip[data-status=idle]")).ToHaveTextAsync("Disabled");
            await Expect(off.GetByRole(AriaRole.Rowheader)).ToContainTextAsync("https://discord.com/api/webhooks/987654/••••");

            // Each event column is ticked for the alerts that hear of it.
            foreach (var (label, heard) in new[] { ("A file finished", false), ("A file failed for good", true), ("Anything failed for good", true), ("Anything finished", false) })
            {
                await Expect(hook.GetByLabel($"E2E hook: {label}")).ToBeCheckedAsync(new() { Checked = heard });
            }

            foreach (var (label, heard) in new[] { ("A file finished", true), ("A file failed for good", false), ("Anything failed for good", true), ("Anything finished", false) })
            {
                await Expect(off.GetByLabel($"E2E discord: {label}")).ToBeCheckedAsync(new() { Checked = heard });
            }

            Assert.DoesNotContain(Token, await page.ContentAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await admin.DeleteWithCsrfAsync($"{Route}/{webhook}");
            await admin.DeleteWithCsrfAsync($"{Route}/{discord}");
        }
    }

    [E2EFact]
    public async Task Removing_an_alert_says_Removing_while_it_works_and_one_that_is_already_gone_keeps_the_question_open_with_the_reason()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var removed = await CreateAsync(admin, "E2E removed", "webhook", "https://hooks.example.test/removed", enabled: true);
        var gone = await CreateAsync(admin, "E2E gone", "webhook", "https://hooks.example.test/gone", enabled: true);
        var kept = await CreateAsync(admin, "E2E kept", "webhook", "https://hooks.example.test/kept", enabled: true);
        try
        {
            var page = await NewPageAsync();
            await page.RunAndWaitForResponseAsync(
                () => Navigation.EnsureSignedInAsync(page, BaseUrl),
                response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
            await Navigation.OpenTabAsync(page, "Connections", "Alerts");
            var dialog = page.GetByTestId(DialogId);

            // While Weir removes it, the row's button and the question's both say so and wait.
            await RecordWordsAsync(RowOf(page, "E2E removed").GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Remove|Removing…)$") }), "__rowWords");
            await RowOf(page, "E2E removed").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
            await RecordWordsAsync(page.GetByTestId($"{DialogId}-confirm"), "__dialogWords");
            await page.GetByTestId($"{DialogId}-confirm").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(RowOf(page, "E2E removed")).ToHaveCountAsync(0);
            Assert.Equal(["Remove", "Removing…"], await WordsAsync(page, "__rowWords"));
            Assert.Equal(["Remove alert", "Removing…"], await WordsAsync(page, "__dialogWords"));

            // Asked about an alert that someone else removes meanwhile, the question stays and says why it could not be done.
            await RowOf(page, "E2E gone").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
            await Expect(dialog).ToContainTextAsync("Remove E2E gone?");
            await admin.DeleteWithCsrfAsync($"{Route}/{gone}");
            await Expect(RowOf(page, "E2E gone")).ToHaveCountAsync(0);
            await page.GetByTestId($"{DialogId}-confirm").ClickAsync();
            await Expect(dialog.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
            Assert.False(string.IsNullOrWhiteSpace(await dialog.GetByRole(AriaRole.Alert).InnerTextAsync()));
            await Expect(dialog).ToBeVisibleAsync();
            await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(RowOf(page, "E2E kept")).ToHaveCountAsync(1);
        }
        finally
        {
            foreach (var id in new[] { removed, gone, kept })
            {
                await admin.DeleteWithCsrfAsync($"{Route}/{id}");
            }
        }
    }

    private static string ToTitleCase(string text) => text.Length == 0 ? text : string.Concat(text[..1].ToUpperInvariant(), text[1..].ToLowerInvariant());

    [E2EFact]
    public async Task Adding_an_alert_asks_for_the_address_to_send_to()
    {
        var page = await NewPageAsync();
        await OpenAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Add alert", Exact = true }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Webhook URL" })).ToBeVisibleAsync();
    }

    private static ILocator RowOf(IPage page, string label) => page.GetByRole(AriaRole.Row).Filter(new() { Has = page.GetByRole(AriaRole.Rowheader, new() { Name = label }) });

    private async Task OpenAsync(IPage page)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Alerts");
        await Expect(page.GetByTestId("suite-settings-notifications")).ToBeVisibleAsync();
    }

    private static async Task<long> CreateAsync(WeirClient admin, string label, string provider, string url, bool enabled, string[]? events = null)
    {
        var created = await admin.PostWithCsrfAsync(Route, new JsonObject
        {
            ["label"] = label,
            ["provider"] = provider,
            ["url"] = url,
            ["events"] = new JsonArray([.. (events ?? ["job_failed", "processing_job_failed"]).Select(name => (JsonNode)name)]),
            ["enabled"] = enabled,
        });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    private static async Task RecordWordsAsync(ILocator element, string name) =>
        await element.EvaluateAsync(
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

    private static async Task<List<string>> WordsAsync(IPage page, string name) =>
        [.. (await page.EvaluateAsync<JsonElement>("name => window[name]", name)).EnumerateArray().Select(item => item.GetString()!)];
}
