using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>Setup › Connections › Media managers as a person works it: the Add panel and a connection's card.</summary>
public static class MediaManagersPage
{
    public const string Connections = $"{WeirClient.Api}/media-managers/connections";

    public static async Task OpenAsync(IPage page, string baseUrl)
    {
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Media managers");
        await Expect(page.GetByTestId("suite-settings-media-managers")).ToBeVisibleAsync();
    }

    /// <summary>The card of the connection whose title (its name, a dot and its nickname) starts with this.</summary>
    public static ILocator Card(IPage page, string title) =>
        page.GetByTestId("media-manager-card").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = title }) });

    public static ILocator AddPanel(IPage page) => page.GetByTestId("media-manager-add-panel");

    /// <summary>Add media manager, and the panel it opens.</summary>
    public static async Task<ILocator> OpenAddAsync(IPage page)
    {
        await page.GetByTestId("media-manager-add").ClickAsync();
        var panel = AddPanel(page);
        await Expect(panel).ToBeVisibleAsync();
        return panel;
    }

    /// <summary>Connects a fake manager through the API, as another client would; returns its id.</summary>
    public static async Task<long> ConnectAsync(WeirClient admin, string kind, string baseUrl, string apiKey, string? nickname = null)
    {
        var body = new JsonObject { ["kind"] = kind, ["base_url"] = baseUrl, ["api_key"] = apiKey, ["enabled"] = true };
        if (nickname is not null)
        {
            body["nickname"] = nickname;
        }

        var created = await admin.PostWithCsrfAsync(Connections, body);
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    /// <summary>The connection the server holds, as it reports it.</summary>
    public static async Task<JsonObject> ReadAsync(WeirClient admin, long id)
    {
        var listed = await admin.GetAsync(Connections);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return listed.Elements.Select(row => row!.AsObject()).Single(row => (long)row["id"]! == id);
    }
}
