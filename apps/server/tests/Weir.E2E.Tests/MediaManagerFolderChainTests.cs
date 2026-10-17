using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Connections › Media managers, a card's folder chain: each workflow the manager feeds, whether it is ready end to end, and the way into its editor.</summary>
public sealed class MediaManagerFolderChainTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E chained";

    [E2EFact]
    public async Task A_workflow_Sonarr_feeds_says_Needs_attention_until_everything_lines_up_and_then_Not_verified_where_Sonarr_cannot_say_a_folder()
    {
        using var folders = new TemporaryFolder();
        var completed = Directory.CreateDirectory(Path.Join(folders.Path, "completed")).FullName;
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var rig = await SonarrFedWorkflow.StartAsync(BaseUrl, Name, completed, watched, output);
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var card = MediaManagersPage.Card(page, rig.SonarrName);

        await Expect(card.GetByTestId("media-manager-libraries")).ToContainTextAsync($"Workflows it feeds: {Name}");
        await Expect(Row(card).Locator("[data-status]")).ToHaveTextAsync("Needs attention");

        // The download client saves where the workflow watches and Sonarr has the mapping, but Sonarr does not say where its category saves.
        var body = LibraryBodies.Unchanged(await WorkflowEditor.ReadAsync(rig.Admin, rig.WorkflowId));
        body["watched_folder"] = completed;
        var saved = await rig.Admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{rig.WorkflowId}", body);
        Assert.True(saved.Status == System.Net.HttpStatusCode.OK, saved.ToString());
        rig.Sonarr.Route("GET", "/api/v3/remotepathmapping", new JsonArray(new JsonObject
        {
            ["host"] = new Uri(rig.Client.BaseUrl).Host,
            ["remotePath"] = completed + Path.DirectorySeparatorChar,
            ["localPath"] = output + Path.DirectorySeparatorChar,
            ["id"] = 1,
        }));
        await page.ReloadAsync();
        card = MediaManagersPage.Card(page, rig.SonarrName);
        await Expect(Row(card).Locator("[data-status=attention]")).ToHaveTextAsync("Not verified");

        await Row(card).GetByRole(AriaRole.Link, new() { Name = "See its folder chain" }).ClickAsync();
        var form = WorkflowEditor.Form(page);
        await Expect(form.GetByRole(AriaRole.Heading, new() { Name = "Edit workflow", Exact = true })).ToBeVisibleAsync();
        await Expect(WorkflowEditor.Text(form, "Name")).ToHaveValueAsync(Name);
    }

    [E2EFact]
    public async Task A_workflow_Deluno_feeds_says_Ready_when_its_folders_line_up()
    {
        using var rig = new TemporaryFolder();
        await using var arranged = await LinkedDeluno.StartAsync(BaseUrl, rig);
        var page = await NewPageAsync();
        await MediaManagersPage.OpenAsync(page, BaseUrl);
        var card = MediaManagersPage.Card(page, arranged.Name);

        await Expect(Row(card).Locator("[data-status=done]")).ToHaveTextAsync("Ready");
        await Expect(Row(card)).ToContainTextAsync("Movies");
    }

    private static ILocator Row(ILocator card) => card.GetByTestId("media-manager-folder-chain").Locator("p");

    [E2EFact]
    public async Task A_manager_with_no_workflow_linked_says_so_and_offers_to_add_one()
    {
        using var sonarr = FakeManager.StartArr("sonarr");
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await MediaManagersPage.ConnectAsync(admin, "sonarr", sonarr.BaseUrl, sonarr.ApiKey, "E2E alone");
        try
        {
            var page = await NewPageAsync();
            await MediaManagersPage.OpenAsync(page, BaseUrl);
            var card = MediaManagersPage.Card(page, "Sonarr on 127.0.0.1 · E2E alone");

            await Expect(card.GetByTestId("media-manager-libraries")).ToHaveTextAsync("No workflow is linked to it yet.");
            await Expect(card.GetByTestId("media-manager-folder-chain")).ToHaveCountAsync(0);
            await card.GetByRole(AriaRole.Link, new() { Name = "Add a workflow from Sonarr on 127.0.0.1 · E2E alone" }).ClickAsync();
            await Expect(page.GetByTestId("add-workflow-choice")).ToBeVisibleAsync();
        }
        finally
        {
            await ManagerConnections.RemoveAsync(admin, id);
        }
    }
}
