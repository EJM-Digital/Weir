using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Connections › Alerts, Remove: the question names the exact alert, and Keep it, Escape and a click outside the dialog leave it in
/// place and send nothing; only Remove alert removes it, and only that one, with a request that carries the page's CSRF token.
/// </summary>
public sealed class RemoveAlertDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const string DialogId = "notification-channel-remove-confirm";
    private const string Route = $"{WeirClient.Api}/suite/notification-channels";
    private const string Chosen = "E2E alert to remove";
    private const string Other = "E2E alert to keep";

    [E2EFact]
    public async Task Keep_it_Escape_and_a_click_outside_the_dialog_leave_the_alert_alone_and_send_nothing()
    {
        await using var arranged = await TwoAlertsAsync();
        var (page, log) = await OpenAlertsAsync();
        var dialog = page.GetByTestId(DialogId);
        var before = await ListedAsync(arranged.Admin);

        await OpenDialogAsync(page);
        await Expect(dialog).ToContainTextAsync($"Remove {Chosen}?");
        var mark = log.Mark();
        await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RemoveButton(page)).ToBeFocusedAsync();

        await OpenDialogAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RemoveButton(page)).ToBeFocusedAsync();

        // The dimmed area around the card does nothing: the question stays until it is answered.
        await OpenDialogAsync(page);
        await Dialogs.ClickOutsideAsync(dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        log.AssertNothingSentSince(mark);
        await Expect(RowOf(page, Chosen)).ToHaveCountAsync(1);
        Assert.Equal(before, await ListedAsync(arranged.Admin));
    }

    [E2EFact]
    public async Task Remove_alert_sends_one_request_with_the_CSRF_token_for_that_alert_and_leaves_the_other()
    {
        await using var arranged = await TwoAlertsAsync();
        var (page, log) = await OpenAlertsAsync();
        var dialog = page.GetByTestId(DialogId);
        var before = await ListedAsync(arranged.Admin);
        await OpenDialogAsync(page);
        var mark = log.Mark();

        await page.GetByTestId($"{DialogId}-confirm").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RowOf(page, Chosen)).ToHaveCountAsync(0);
        await Expect(RowOf(page, Other)).ToHaveCountAsync(1);
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal($"{Route}/{arranged.ChosenId}", sent.PathAndQuery);
        Assert.False(string.IsNullOrEmpty(sent.CsrfHeader), "The removal carries the page's CSRF token in the X-CSRF-Token header.");
        Assert.Equal(before.Where(line => !line.StartsWith($"{arranged.ChosenId} ", StringComparison.Ordinal)), await ListedAsync(arranged.Admin));
    }

    private static ILocator RowOf(IPage page, string label) => page.GetByRole(AriaRole.Row).Filter(new() { Has = page.GetByRole(AriaRole.Rowheader, new() { Name = label }) });

    private static ILocator RemoveButton(IPage page) => RowOf(page, Chosen).GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true });

    private static async Task OpenDialogAsync(IPage page)
    {
        await RemoveButton(page).ClickAsync();
        await Expect(page.GetByTestId($"{DialogId}-cancel")).ToBeFocusedAsync();
    }

    private async Task<(IPage Page, RequestLog Log)> OpenAlertsAsync()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Alerts");
        await Expect(RowOf(page, Chosen)).ToHaveCountAsync(1);
        await Expect(RowOf(page, Other)).ToHaveCountAsync(1);
        return (page, RequestLog.Watch(page));
    }

    /// <summary>What Weir holds for each alert, one line each: what a removal could take away or change.</summary>
    private static async Task<IReadOnlyList<string>> ListedAsync(WeirClient admin)
    {
        var listed = await admin.GetAsync(Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return
        [
            .. listed.Fields["items"]!.AsArray()
                .Select(row => row!.AsObject())
                .OrderBy(row => (long)row["id"]!)
                .Select(row => $"{row["id"]} {row["label"]} {row["provider"]} on={row["enabled"]} events={row["events"]!.ToJsonString()}"),
        ];
    }

    private async Task<Alerts> TwoAlertsAsync()
    {
        var admin = await AdminSession.OpenAsync(BaseUrl);
        var alerts = new Alerts(admin);
        try
        {
            alerts.ChosenId = await alerts.CreateAsync(Chosen);
            await alerts.CreateAsync(Other);
            return alerts;
        }
        catch
        {
            await alerts.DisposeAsync();
            throw;
        }
    }

    /// <summary>Two alerts that post to a webhook, one of which the test removes. Disposing takes both away again.</summary>
    private sealed class Alerts(WeirClient admin) : IAsyncDisposable
    {
        private readonly List<long> _ids = [];

        public WeirClient Admin { get; } = admin;

        public long ChosenId { get; set; }

        public async Task<long> CreateAsync(string label)
        {
            var created = await Admin.PostWithCsrfAsync(Route, new JsonObject
            {
                ["label"] = label,
                ["provider"] = "webhook",
                ["url"] = "https://alerts.example.test/hook",
                ["events"] = new JsonArray("job_failed"),
                ["enabled"] = true,
            });
            Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
            var id = (long)created.Fields["id"]!;
            _ids.Add(id);
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var id in _ids)
                {
                    await Admin.DeleteWithCsrfAsync($"{Route}/{id}");
                }
            }
            finally
            {
                Admin.Dispose();
            }
        }
    }
}
