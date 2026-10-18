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
/// Setup › Workflows › File paths, the editor of a workflow linked to Sonarr: what the manager and its download client say, the offer to
/// use the client's folder, the re-read when a folder changes, and a second manager linked through the API that saving keeps.
/// </summary>
public sealed class WorkflowManagerSetupTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E Sonarr fed";

    [E2EFact]
    public async Task The_editor_offers_the_download_clients_folder_as_the_watched_folder_and_keeps_the_output_folder()
    {
        using var folders = new TemporaryFolder();
        var completed = Directory.CreateDirectory(Path.Join(folders.Path, "completed")).FullName;
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var rig = await SonarrFedWorkflow.StartAsync(BaseUrl, Name, completed, watched, output);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var form = await WorkflowEditor.OpenEditAsync(page, rig.WorkflowId);
        await WorkflowEditor.OpenGroupAsync(form, "Media manager");
        var offer = form.GetByTestId("library-download-client-suggestions");

        await Expect(offer).ToContainTextAsync($"qBittorrent on 127.0.0.1's completed-downloads folder is {completed}.");
        await offer.GetByRole(AriaRole.Button, new() { Name = "Use this as the watched folder" }).ClickAsync();

        await Expect(WorkflowEditor.Text(form, "Watched folder")).ToHaveValueAsync(completed);
        await Expect(WorkflowEditor.Text(form, "Output folder")).ToHaveValueAsync(output);
        await Expect(offer).ToHaveCountAsync(0);
        await WorkflowEditor.Save(page).ClickAsync();
        await Expect(form).ToHaveCountAsync(0);
        var saved = await WorkflowEditor.ReadAsync(rig.Admin, rig.WorkflowId);
        Assert.Equal(completed, (string)saved["watched_folder"]!);
        Assert.Equal(output, (string)saved["output_folder"]!);
    }

    [E2EFact]
    public async Task What_the_manager_needs_is_read_again_for_the_new_folders_and_the_manager_is_only_ever_asked_not_changed()
    {
        using var folders = new TemporaryFolder();
        var completed = Directory.CreateDirectory(Path.Join(folders.Path, "completed")).FullName;
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        var newOutput = Directory.CreateDirectory(Path.Join(folders.Path, "output-moved")).FullName;
        await using var rig = await SonarrFedWorkflow.StartAsync(BaseUrl, Name, completed, watched, output);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var form = await WorkflowEditor.OpenEditAsync(page, rig.WorkflowId);
        await WorkflowEditor.OpenGroupAsync(form, "Media manager");
        var setup = form.GetByTestId("library-manager-setup");
        var localPath = setup.GetByRole(AriaRole.Row, new() { Name = "Local Path" });
        await Expect(localPath).ToContainTextAsync(output);

        var reread = page.WaitForRequestAsync(request => request.Url.Contains("/processing/manager-setup", StringComparison.Ordinal) && request.Url.Contains(Uri.EscapeDataString(newOutput), StringComparison.Ordinal));
        await WorkflowEditor.Text(form, "Output folder").FillAsync(newOutput);
        await reread;
        await Expect(localPath).ToContainTextAsync(newOutput);
        await Expect(localPath).Not.ToContainTextAsync(output + Path.DirectorySeparatorChar);

        Assert.All(rig.Sonarr.Requests, request => Assert.Equal("GET", request.Method));
        Assert.NotEmpty(rig.Sonarr.RequestsTo("GET", "/api/v3/downloadclient"));
    }

    [E2EFact]
    public async Task A_second_manager_linked_through_the_API_is_still_linked_after_the_workflow_is_saved_from_the_editor()
    {
        using var folders = new TemporaryFolder();
        var completed = Directory.CreateDirectory(Path.Join(folders.Path, "completed")).FullName;
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var rig = await SonarrFedWorkflow.StartAsync(BaseUrl, Name, completed, watched, output);
        using var second = FakeManager.StartArr("sonarr");
        var connected = await rig.Admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections", new JsonObject
        {
            ["kind"] = "sonarr",
            ["base_url"] = second.BaseUrl,
            ["api_key"] = second.ApiKey,
            ["enabled"] = true,
        });
        Assert.True(connected.Status == HttpStatusCode.Created, connected.ToString());
        var secondId = (long)connected.Fields["id"]!;
        try
        {
            var body = LibraryBodies.Unchanged(await WorkflowEditor.ReadAsync(rig.Admin, rig.WorkflowId));
            body["manager_connection_ids"] = new JsonArray(rig.SonarrId, secondId);
            var linked = await rig.Admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{rig.WorkflowId}", body);
            Assert.True(linked.Status == HttpStatusCode.OK, linked.ToString());
            var page = await NewPageAsync();
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);

            var form = await WorkflowEditor.OpenEditAsync(page, rig.WorkflowId);
            await WorkflowEditor.Text(form, "Name").FillAsync("E2E Sonarr fed twice");
            await WorkflowEditor.Save(page).ClickAsync();
            await Expect(form).ToHaveCountAsync(0);

            var saved = await WorkflowEditor.ReadAsync(rig.Admin, rig.WorkflowId);
            Assert.Equal("E2E Sonarr fed twice", (string)saved["name"]!);
            Assert.Equal([rig.SonarrId, secondId], saved["manager_connection_ids"]!.AsArray().Select(id => (long)id!));
        }
        finally
        {
            await rig.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/libraries/{rig.WorkflowId}");
            await ManagerConnections.RemoveAsync(rig.Admin, secondId);
        }
    }
}
