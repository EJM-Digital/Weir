using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup, Connections and Settings follow Weir live: something changed through the API from outside the page shows on the
/// open screen with no reload.
/// </summary>
public sealed class SetupLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const string Api = WeirClient.Api;
    private const string DestinationsPath = "/api/integrations/processors/download-destinations";

    /// <summary>An admin session of the test's own, so a change reaches Weir the way another client's would.</summary>
    private async Task<WeirClient> SignedInClientAsync()
    {
        var client = new WeirClient(new Uri(BaseUrl));
        await client.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
        return client;
    }

    /// <summary>Signs in, waits until the page has its live stream open, and marks the page so a reload can be told.</summary>
    private async Task<IPage> OpenLiveAsync(Func<IPage, Task> openScreen)
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await openScreen(page);
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        return page;
    }

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    /// <summary>The workflow of this name in the list: each row has one button, named for it, that moves it in the order.</summary>
    private static ILocator Workflow(ILocator list, string name) =>
        list.GetByRole(AriaRole.Button, new() { Name = $"Move {name}", Exact = true });

    private static async Task<JsonObject> CreatedAsync(WeirClient client, string path, JsonObject body)
    {
        var created = await client.PostWithCsrfAsync(path, body);
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return created.Fields;
    }

    [E2EFact]
    public async Task A_media_manager_added_through_the_api_appears_in_Connections_and_follows_its_tests_without_a_reload()
    {
        using var manager = FakeManager.StartArr("sonarr");
        manager.Route("GET", "/api/v3/system/status", new JsonObject(), status: 500);
        using var client = await SignedInClientAsync();
        var page = await OpenLiveAsync(opened => Navigation.OpenTabAsync(opened, "Connections", "Media managers"));
        var cards = page.GetByTestId("media-manager-card");
        await Expect(page.GetByTestId("suite-settings-media-managers")).ToBeVisibleAsync();
        await Expect(cards).ToHaveCountAsync(0);

        var created = await CreatedAsync(client, $"{Api}/media-managers/connections", new JsonObject
        {
            ["kind"] = "sonarr",
            ["base_url"] = manager.BaseUrl,
            ["api_key"] = manager.ApiKey,
            ["enabled"] = true,
        });
        var id = (long)created["id"]!;
        try
        {
            await Expect(cards).ToHaveCountAsync(1);
            var card = cards.First;
            await Expect(card.GetByRole(AriaRole.Heading, new() { Name = (string)created["name"]!, Exact = true })).ToBeVisibleAsync();

            var failed = await client.PostWithCsrfAsync($"{Api}/media-managers/connections/{id}/test");
            Assert.True(failed.Status == HttpStatusCode.OK, failed.ToString());
            await Expect(card.GetByText("Not answering", new() { Exact = true })).ToBeVisibleAsync();

            manager.Route("GET", "/api/v3/system/status", new JsonObject { ["appName"] = "Sonarr", ["version"] = "4.0.0.0" });
            var answered = await client.PostWithCsrfAsync($"{Api}/media-managers/connections/{id}/test");
            Assert.True(answered.Status == HttpStatusCode.OK, answered.ToString());
            await Expect(card.GetByText("Answering", new() { Exact = true })).ToBeVisibleAsync();

            var switchedOff = await client.PutWithCsrfAsync($"{Api}/media-managers/connections/{id}", new JsonObject { ["enabled"] = false });
            Assert.True(switchedOff.Status == HttpStatusCode.OK, switchedOff.ToString());
            await Expect(card.GetByText("Off", new() { Exact = true })).ToBeVisibleAsync();
        }
        finally
        {
            var removed = await client.DeleteWithCsrfBodyAsync($"{Api}/media-managers/connections/{id}");
            Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        }

        await Expect(cards).ToHaveCountAsync(0);
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task A_download_client_added_through_the_api_appears_in_Connections_without_a_reload()
    {
        using var client = await SignedInClientAsync();
        var page = await OpenLiveAsync(opened => Navigation.OpenTabAsync(opened, "Connections", "Download clients"));
        var section = page.GetByTestId("suite-settings-download-clients-tab");
        await Expect(section).ToBeVisibleAsync();

        var created = await CreatedAsync(client, $"{Api}/download-clients/connections", new JsonObject
        {
            ["kind"] = "qbittorrent",
            ["base_url"] = "http://qbittorrent.invalid:8080",
            ["username"] = "admin",
            ["password"] = "adminadmin",
            ["enabled"] = true,
        });
        var id = (long)created["id"]!;
        try
        {
            await Expect(section.GetByRole(AriaRole.Heading, new() { Name = (string)created["name"]!, Exact = true })).ToBeVisibleAsync();
        }
        finally
        {
            var removed = await client.DeleteWithCsrfBodyAsync($"{Api}/download-clients/connections/{id}");
            Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        }

        await Expect(section.GetByRole(AriaRole.Heading, new() { Name = (string)created["name"]!, Exact = true })).ToHaveCountAsync(0);
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task A_workflow_the_Deluno_sync_sets_up_appears_in_Workflows_without_a_reload()
    {
        using var rig = new TemporaryFolder();
        var saveFolder = Directory.CreateDirectory(Path.Join(rig.Path, "Completed", "Anime")).FullName;
        var readyFolder = Directory.CreateDirectory(Path.Join(rig.Path, "Ready", "Anime")).FullName;
        using var deluno = FakeManager.StartDeluno([new JsonObject
        {
            ["id"] = "lib-anime",
            ["name"] = "Anime",
            ["mediaType"] = "tv",
            ["rootPath"] = "/library/lib-anime",
            ["importWorkflow"] = "refine-before-import",
            ["processorOutputPath"] = readyFolder,
            ["downloadsPath"] = string.Empty,
        }]);
        deluno.Route("GET", DestinationsPath, new JsonObject
        {
            ["libraries"] = new JsonArray(new JsonObject
            {
                ["libraryId"] = "lib-anime",
                ["libraryName"] = "Anime",
                ["downloadsPath"] = string.Empty,
                ["processorOutputPath"] = readyFolder,
                ["destinations"] = new JsonArray(new JsonObject
                {
                    ["downloadClientName"] = "qBittorrent",
                    ["category"] = "weir",
                    ["categoryKind"] = "category",
                    ["saveFolder"] = saveFolder,
                    ["savedBy"] = "client-category",
                    ["status"] = "ok",
                    ["message"] = string.Empty,
                }),
                ["processorConnection"] = new JsonObject { ["pathMappings"] = new JsonArray() },
            }),
        });
        using var client = await SignedInClientAsync();
        var page = await OpenLiveAsync(opened => Navigation.OpenTabAsync(opened, "Workflows", "File paths"));
        var list = page.GetByTestId("processing-libraries-section");
        var syncedNote = list.GetByText("Kept in step with it.", new() { Exact = true });
        await Expect(list).ToBeVisibleAsync();
        await Expect(syncedNote).ToHaveCountAsync(0);
        await Expect(Workflow(list, "Anime")).ToHaveCountAsync(0);

        // The sync sets the unconfigured TV workflow up and the test renames it; the server is shared, so it is put back as it was.
        var untouched = (await client.GetAsync($"{Api}/processing/libraries")).Elements
            .Select(row => row!.AsObject())
            .Single(row => (string)row["name"]! == "TV");
        var untouchedId = (long)untouched["id"]!;
        var untouchedBody = LibraryBodies.Unchanged(untouched);

        var delunoId = (long)(await CreatedAsync(client, $"{Api}/media-managers/connections", new JsonObject
        {
            ["kind"] = "deluno",
            ["base_url"] = deluno.BaseUrl,
            ["api_key"] = deluno.ApiKey,
            ["enabled"] = true,
        }))["id"]!;
        try
        {
            // Weir's own sync reads the folders Deluno reports and sets the unconfigured TV workflow up from them.
            await Expect(Workflow(list, "Anime")).ToBeVisibleAsync();
            await Expect(syncedNote).ToHaveCountAsync(1);

            var tv = (await client.GetAsync($"{Api}/processing/libraries")).Elements
                .Select(row => row!.AsObject())
                .Single(row => (string)row["name"]! == "Anime");
            var renamed = LibraryBodies.Unchanged(tv);
            renamed["name"] = "Anime shows";
            var saved = await client.PutWithCsrfAsync($"{Api}/processing/libraries/{(long)tv["id"]!}", renamed);
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());

            await Expect(Workflow(list, "Anime shows")).ToBeVisibleAsync();
            await Expect(Workflow(list, "Anime")).ToHaveCountAsync(0);
        }
        finally
        {
            await ManagerConnections.RemoveAsync(client, delunoId);
            var restored = await client.PutWithCsrfAsync($"{Api}/processing/libraries/{untouchedId}", untouchedBody);
            Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        }

        await Expect(Workflow(list, "TV")).ToBeVisibleAsync();
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task Settings_saved_through_the_api_show_in_Performance_without_a_reload()
    {
        using var client = await SignedInClientAsync();
        var page = await OpenLiveAsync(opened => Navigation.OpenTabAsync(opened, "Performance", "Speed"));
        var filesAtOnce = page.GetByRole(AriaRole.Group, new() { Name = "Files at once", Exact = true });
        await Expect(filesAtOnce.GetByRole(AriaRole.Button, new() { Name = "3", Exact = true })).ToHaveAttributeAsync("aria-pressed", "false");

        var saved = await client.PutWithCsrfAsync($"{Api}/processing/operator-settings", new JsonObject { ["max_concurrent_files"] = 3 });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());

        await Expect(filesAtOnce.GetByRole(AriaRole.Button, new() { Name = "3", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task A_backup_made_through_the_api_appears_in_Backups_without_a_reload()
    {
        using var client = await SignedInClientAsync();
        var page = await OpenLiveAsync(opened => Navigation.OpenTabAsync(opened, "System", "Backups"));
        var restoreButtons = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Restore the backup taken") });
        await Expect(page.GetByText("No automatic snapshots yet.", new() { Exact = true })).ToBeVisibleAsync();

        var made = await client.PostWithCsrfAsync($"{Api}/suite/configuration-backups");
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());

        await Expect(restoreButtons).ToHaveCountAsync(1);
        await Expect(page.GetByText("No automatic snapshots yet.", new() { Exact = true })).ToHaveCountAsync(0);
        await AssertNotReloadedAsync(page);
    }
}
