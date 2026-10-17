using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Connections › Download clients, Remove: the question names the exact client, and Keep it, Escape and a click outside the dialog
/// leave the client in place and send nothing; only Remove download client removes it, and only that one.
/// </summary>
public sealed class RemoveDownloadClientDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const string DialogId = "download-client-remove-confirm";
    private const string Route = $"{WeirClient.Api}/download-clients/connections";

    [E2EFact]
    public async Task Keep_it_Escape_and_a_click_outside_the_dialog_leave_the_client_alone_and_send_nothing()
    {
        await using var arranged = await TwoClientsAsync();
        var (page, log) = await OpenDownloadClientsAsync(arranged);
        var dialog = page.GetByTestId(DialogId);
        var before = await ListedAsync(arranged.Admin);

        await OpenDialogAsync(page, arranged.Chosen.Name);
        await Expect(dialog).ToContainTextAsync($"Remove {arranged.Chosen.Name}?");
        var mark = log.Mark();
        await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RemoveButton(page, arranged.Chosen.Name)).ToBeFocusedAsync();

        await OpenDialogAsync(page, arranged.Chosen.Name);
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RemoveButton(page, arranged.Chosen.Name)).ToBeFocusedAsync();

        // The dimmed area around the card does nothing: the question stays until it is answered.
        await OpenDialogAsync(page, arranged.Chosen.Name);
        await Dialogs.ClickOutsideAsync(dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(RemoveButton(page, arranged.Chosen.Name)).ToBeFocusedAsync();

        log.AssertNothingSentSince(mark);
        await Expect(CardOf(page, arranged.Chosen.Name)).ToHaveCountAsync(1);
        Assert.Equal(before, await ListedAsync(arranged.Admin));
    }

    [E2EFact]
    public async Task Remove_download_client_sends_one_request_for_that_client_and_leaves_the_other()
    {
        await using var arranged = await TwoClientsAsync();
        var (page, log) = await OpenDownloadClientsAsync(arranged);
        var dialog = page.GetByTestId(DialogId);
        var before = await ListedAsync(arranged.Admin);
        await OpenDialogAsync(page, arranged.Chosen.Name);
        var mark = log.Mark();

        await page.GetByTestId($"{DialogId}-confirm").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(CardOf(page, arranged.Chosen.Name)).ToHaveCountAsync(0);
        await Expect(CardOf(page, arranged.Other.Name)).ToHaveCountAsync(1);
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal($"{Route}/{arranged.Chosen.Id}", sent.PathAndQuery);
        Assert.False(string.IsNullOrEmpty((string?)JsonNode.Parse(sent.Body!)!["csrf_token"]), "The removal carries the page's CSRF token.");
        Assert.Equal(before.Where(line => !line.StartsWith($"{arranged.Chosen.Id} ", StringComparison.Ordinal)), await ListedAsync(arranged.Admin));
    }

    private static ILocator RemoveButton(IPage page, string name) => CardOf(page, name).GetByTestId("download-client-remove");

    private static ILocator CardOf(IPage page, string name) =>
        page.GetByTestId("download-client-card").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true }) });

    private static async Task OpenDialogAsync(IPage page, string name)
    {
        await RemoveButton(page, name).ClickAsync();
        await Expect(page.GetByTestId($"{DialogId}-cancel")).ToBeFocusedAsync();
    }

    private async Task<(IPage Page, RequestLog Log)> OpenDownloadClientsAsync(Arranged arranged)
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Download clients");
        await Expect(CardOf(page, arranged.Chosen.Name)).ToHaveCountAsync(1);
        await Expect(CardOf(page, arranged.Other.Name)).ToHaveCountAsync(1);
        return (page, RequestLog.Watch(page));
    }

    /// <summary>What Weir holds for each download client, one line each: what a removal could take away or change, and not the result of its last test.</summary>
    private static async Task<IReadOnlyList<string>> ListedAsync(WeirClient admin)
    {
        var listed = await admin.GetAsync(Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return
        [
            .. listed.Elements
                .Select(row => row!.AsObject())
                .OrderBy(row => (long)row["id"]!)
                .Select(row => $"{row["id"]} {row["kind"]} {row["name"]} on={row["enabled"]} {row["base_url"]} user={row["username"]} password={row["password_is_saved"]} key={row["api_key_is_saved"]}"),
        ];
    }

    private async Task<Arranged> TwoClientsAsync()
    {
        var admin = await AdminSession.OpenAsync(BaseUrl);
        var chosen = FakeDownloadClient.Start("qbittorrent", "weir-qbit", "qbit-password");
        var other = FakeDownloadClient.Start("deluge", username: null, secret: "deluge-password");
        var arranged = new Arranged(admin, chosen, other);
        try
        {
            await arranged.ConnectAsync();
            return arranged;
        }
        catch
        {
            await arranged.DisposeAsync();
            throw;
        }
    }

    /// <summary>Two download clients connected to Weir, one of which the test removes. Disposing takes both away again.</summary>
    private sealed class Arranged(WeirClient admin, FakeDownloadClient chosen, FakeDownloadClient other) : IAsyncDisposable
    {
        private readonly List<long> _ids = [];

        public WeirClient Admin { get; } = admin;

        public Connected Chosen { get; private set; } = null!;

        public Connected Other { get; private set; } = null!;

        public async Task ConnectAsync()
        {
            Chosen = await ConnectAsync(new JsonObject { ["kind"] = "qbittorrent", ["base_url"] = chosen.BaseUrl, ["username"] = "weir-qbit", ["password"] = "qbit-password" });
            Other = await ConnectAsync(new JsonObject { ["kind"] = "deluge", ["base_url"] = other.BaseUrl, ["password"] = "deluge-password" });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var id in _ids)
                {
                    if ((await Admin.GetAsync($"{Route}/{id}")).Status == HttpStatusCode.OK)
                    {
                        await Admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
                    }
                }
            }
            finally
            {
                Admin.Dispose();
                chosen.Dispose();
                other.Dispose();
            }
        }

        private async Task<Connected> ConnectAsync(JsonObject body)
        {
            var created = await Admin.PostWithCsrfAsync(Route, body);
            Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
            var connected = new Connected((long)created.Fields["id"]!, (string)created.Fields["name"]!);
            _ids.Add(connected.Id);
            return connected;
        }
    }

    private sealed record Connected(long Id, string Name);
}
