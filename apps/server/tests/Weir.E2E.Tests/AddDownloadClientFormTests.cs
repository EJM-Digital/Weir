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
/// Connections › Download clients, Add: the form asks for what the chosen client's own login uses (an API key for SABnzbd, a password
/// alone for Deluge, a user name and password for the rest), will not be submitted without an address or a secret its client cannot do
/// without, and adds a client that really answers when it is tested. Each kind is added against a fake client of that kind
/// that only lets in the credentials it was set to expect.
/// </summary>
public sealed class AddDownloadClientFormTests(E2EServer server) : E2ETestBase(server)
{
    private const string Route = $"{WeirClient.Api}/download-clients/connections";
    private const string UnusedAddress = "http://192.0.2.10:8080";

    private static readonly Kind[] Kinds =
    [
        new("sabnzbd", "SABnzbd", Login.ApiKey, Username: null, Secret: "sabnzbd-api-key"),
        new("nzbget", "NZBGet", Login.UsernameAndPassword, Username: "weir-nzbget", Secret: "nzbget-password"),
        new("qbittorrent", "qBittorrent", Login.UsernameAndPassword, Username: "weir-qbit", Secret: "qbit-password"),
        new("deluge", "Deluge", Login.PasswordOnly, Username: null, Secret: "deluge-password"),
        new("transmission", "Transmission", Login.UsernameAndPassword, Username: "weir-transmission", Secret: "transmission-password"),
    ];

    private enum Login
    {
        ApiKey,
        UsernameAndPassword,
        PasswordOnly,
    }

    private sealed record Kind(string Value, string Label, Login Login, string? Username, string Secret);

    [E2EFact]
    public async Task Each_kind_asks_for_exactly_the_fields_its_login_uses()
    {
        var (page, panel) = await OpenAddPanelAsync();

        foreach (var kind in Kinds)
        {
            await Fields.ChooseKindAsync(page, kind.Value);

            await Expect(Fields.ApiKey(panel)).ToHaveCountAsync(kind.Login == Login.ApiKey ? 1 : 0);
            await Expect(Fields.Username(panel)).ToHaveCountAsync(kind.Login == Login.UsernameAndPassword ? 1 : 0);
            await Expect(Fields.Password(panel)).ToHaveCountAsync(kind.Login == Login.ApiKey ? 0 : 1);
            await Expect(Fields.Address(panel)).ToBeVisibleAsync();
            await Expect(Fields.Nickname(panel)).ToBeVisibleAsync();
        }
    }

    [E2EFact]
    public async Task The_form_cannot_be_submitted_without_an_address_or_a_secret_its_client_cannot_do_without_and_sends_nothing()
    {
        var (page, panel) = await OpenAddPanelAsync();
        var log = RequestLog.Watch(page);
        var mark = log.Mark();
        var save = Fields.Save(panel);
        var reason = panel.GetByTestId("download-client-save-reason");

        // The form says what is missing, and the button is described by it.
        await Expect(reason).ToHaveTextAsync("Enter the address and API key to add this client.");
        await Expect(save).ToHaveAttributeAsync("aria-describedby", await reason.GetAttributeAsync("id") ?? string.Empty);

        // No address: no kind can be added, whatever secret it has.
        foreach (var kind in Kinds)
        {
            await Fields.ChooseKindAsync(page, kind.Value);
            await Fields.FillSecretsAsync(panel, kind);
            await Expect(save).ToBeDisabledAsync();
        }

        // SABnzbd needs its API key and Deluge its password; the other three can run with neither.
        await Fields.Address(panel).FillAsync(UnusedAddress);
        foreach (var kind in Kinds)
        {
            await Fields.ChooseKindAsync(page, kind.Value);
            await Fields.ClearSecretsAsync(panel);
            if (kind.Login == Login.UsernameAndPassword)
            {
                await Expect(save).ToBeEnabledAsync();
            }
            else
            {
                await Expect(save).ToBeDisabledAsync();
                await Fields.Address(panel).PressAsync("Enter");
                await Fields.FillSecretsAsync(panel, kind);
                await Expect(save).ToBeEnabledAsync();
            }

            await Expect(reason).ToHaveCountAsync(0);
        }

        // Enter in the address with the secret missing is a submit the form refuses.
        await Fields.ChooseKindAsync(page, "sabnzbd");
        await Fields.ClearSecretsAsync(panel);
        await Fields.Address(panel).PressAsync("Enter");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId(Fields.PanelId)).ToHaveCountAsync(0);

        log.AssertNothingSentSince(mark);
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        Assert.Empty(await ConnectionsAddressedAsync(admin, UnusedAddress));
    }

    [E2EFact]
    public async Task Each_kind_is_added_with_the_credentials_typed_and_answers_when_it_is_tested()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var fakes = Kinds.ToDictionary(kind => kind.Value, kind => FakeDownloadClient.Start(kind.Value, kind.Username, kind.Secret));
        var page = await NewPageAsync();
        await OpenDownloadClientsAsync(page);
        var added = new List<long>();
        try
        {
            foreach (var kind in Kinds)
            {
                var fake = fakes[kind.Value];
                var panel = await OpenPanelAsync(page);
                await Fields.ChooseKindAsync(page, kind.Value);
                await Fields.Address(panel).FillAsync(fake.BaseUrl);
                await Fields.FillSecretsAsync(panel, kind);
                await Expect(Fields.Save(panel)).ToBeEnabledAsync();

                await Fields.Save(panel).ClickAsync();

                await Expect(page.GetByTestId(Fields.PanelId)).ToHaveCountAsync(0);
                var row = Assert.Single(await ConnectionsAddressedAsync(admin, fake.BaseUrl));
                added.Add((long)row["id"]!);
                Assert.Equal(kind.Value, (string)row["kind"]!);
                Assert.Equal(kind.Username, (string?)row["username"]);
                Assert.Equal(kind.Login != Login.ApiKey, (bool)row["password_is_saved"]!);
                Assert.Equal(kind.Login == Login.ApiKey, (bool)row["api_key_is_saved"]!);
                var name = (string)row["name"]!;
                await Expect(page.GetByTestId("download-client-created-note")).ToContainTextAsync($"{name} is connected.");

                var card = page.GetByTestId("download-client-card").Filter(new() { HasText = name });
                await Expect(card.GetByTestId("download-client-status")).ToContainTextAsync("Not tested yet");
                var attemptsBefore = fake.Attempts.Count;
                await card.GetByTestId("download-client-test").ClickAsync();

                await Expect(card.GetByTestId("download-client-status")).ToContainTextAsync("Answering");
                var attempts = fake.Attempts.Skip(attemptsBefore).ToList();
                Assert.NotEmpty(attempts);
                Assert.All(attempts, attempt =>
                {
                    Assert.True(attempt.Accepted);
                    Assert.Equal(kind.Username, attempt.Username);
                    Assert.Equal(kind.Secret, attempt.Secret);
                });
            }
        }
        finally
        {
            foreach (var id in added)
            {
                await admin.DeleteWithCsrfBodyAsync($"{Route}/{id}");
            }

            foreach (var fake in fakes.Values)
            {
                fake.Dispose();
            }
        }
    }

    [E2EFact]
    public async Task What_was_typed_for_a_kind_the_person_moved_away_from_is_not_saved_with_the_one_they_added()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var fake = FakeDownloadClient.Start("sabnzbd", username: null, secret: "sabnzbd-api-key");
        var qbittorrent = Kinds.Single(kind => kind.Value == "qbittorrent");
        var sabnzbd = Kinds.Single(kind => kind.Value == "sabnzbd");
        var (page, panel) = await OpenAddPanelAsync();
        var log = RequestLog.Watch(page);
        var mark = log.Mark();
        await Fields.ChooseKindAsync(page, qbittorrent.Value);
        await Fields.FillSecretsAsync(panel, qbittorrent);
        await Fields.ChooseKindAsync(page, sabnzbd.Value);
        await Fields.Address(panel).FillAsync(fake.BaseUrl);
        await Fields.FillSecretsAsync(panel, sabnzbd);

        await Fields.Save(panel).ClickAsync();

        await Expect(page.GetByTestId(Fields.PanelId)).ToHaveCountAsync(0);
        var row = Assert.Single(await ConnectionsAddressedAsync(admin, fake.BaseUrl));
        try
        {
            var sent = Assert.Single(log.Since(mark), request => request.Method == "POST");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.Equal("sabnzbd-api-key", (string?)body["api_key"]);
            Assert.False(body.ContainsKey("username") && !string.IsNullOrEmpty((string?)body["username"]), sent.Body);
            Assert.False(body.ContainsKey("password") && !string.IsNullOrEmpty((string?)body["password"]), sent.Body);
            Assert.Null((string?)row["username"]);
            Assert.False((bool)row["password_is_saved"]!);
            Assert.True((bool)row["api_key_is_saved"]!);
        }
        finally
        {
            await admin.DeleteWithCsrfBodyAsync($"{Route}/{(long)row["id"]!}");
        }
    }

    /// <summary>The connections Weir holds for this address, as it lists them: a test finds its own and leaves any other alone.</summary>
    private static async Task<IReadOnlyList<JsonObject>> ConnectionsAddressedAsync(WeirClient admin, string address)
    {
        var listed = await admin.GetAsync(Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return [.. listed.Elements.Select(row => row!.AsObject()).Where(row => (string?)row["base_url"] == address)];
    }

    private async Task<(IPage Page, ILocator Panel)> OpenAddPanelAsync()
    {
        var page = await NewPageAsync();
        await OpenDownloadClientsAsync(page);
        return (page, await OpenPanelAsync(page));
    }

    private async Task OpenDownloadClientsAsync(IPage page)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Download clients");
    }

    private static async Task<ILocator> OpenPanelAsync(IPage page)
    {
        await page.GetByTestId("download-client-add").ClickAsync();
        var panel = page.GetByTestId(Fields.PanelId);
        await Expect(panel).ToBeVisibleAsync();
        return panel;
    }

    /// <summary>The add form's fields, by what a person sees them as.</summary>
    private static class Fields
    {
        public const string PanelId = "download-client-add-panel";

        public static ILocator Address(ILocator panel) => panel.GetByTestId("download-client-base-url");

        public static ILocator ApiKey(ILocator panel) => panel.GetByTestId("download-client-api-key");

        public static ILocator Username(ILocator panel) => panel.GetByTestId("download-client-username");

        public static ILocator Password(ILocator panel) => panel.GetByTestId("download-client-password");

        public static ILocator Nickname(ILocator panel) => panel.GetByTestId("download-client-nickname");

        public static ILocator Save(ILocator panel) => panel.GetByTestId("download-client-save");

        public static async Task ChooseKindAsync(IPage page, string kind)
        {
            await page.GetByTestId("download-client-kind").SelectOptionAsync(kind);
            await Expect(page.GetByTestId("download-client-kind")).ToHaveValueAsync(kind);
        }

        public static async Task FillSecretsAsync(ILocator panel, Kind kind)
        {
            switch (kind.Login)
            {
                case Login.ApiKey:
                    await ApiKey(panel).FillAsync(kind.Secret);
                    break;
                case Login.UsernameAndPassword:
                    await Username(panel).FillAsync(kind.Username!);
                    await Password(panel).FillAsync(kind.Secret);
                    break;
                default:
                    await Password(panel).FillAsync(kind.Secret);
                    break;
            }
        }

        public static async Task ClearSecretsAsync(ILocator panel)
        {
            foreach (var field in new[] { ApiKey(panel), Username(panel), Password(panel) })
            {
                if (await field.CountAsync() > 0)
                {
                    await field.FillAsync(string.Empty);
                }
            }
        }
    }
}
