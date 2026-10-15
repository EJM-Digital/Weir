using System.Net;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

public sealed class DelunoOwnedWorkflowTests(E2EServer server) : E2ETestBase(server)
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";

    [E2EFact]
    public async Task A_workflow_set_up_from_Deluno_shows_its_folders_as_Deluno_s_until_it_is_unlinked()
    {
        using var rig = new TemporaryFolder();
        await using var arranged = await LinkedDeluno.StartAsync(BaseUrl, rig);
        // A connection is named after its kind and the host in its address, and the editor says whose a folder is by that name.
        var ownedNote = $"From {arranged.Name}; change it in {arranged.Name}.";
        var page = await NewPageAsync();

        await OpenWorkflowEditorAsync(page, arranged.MoviesId);
        var form = page.GetByTestId("processing-library-form");
        var watched = form.GetByRole(AriaRole.Textbox, new() { Name = "Watched folder" });
        var output = form.GetByRole(AriaRole.Textbox, new() { Name = "Output folder" });
        var work = form.GetByRole(AriaRole.Textbox, new() { Name = "Work folder" });

        // Deluno owns the watched and output folders; the work folder stays the person's.
        await Expect(watched).ToHaveValueAsync(arranged.SaveFolder);
        await Expect(output).ToHaveValueAsync(arranged.ReadyFolder);
        await Expect(watched).Not.ToBeEditableAsync();
        await Expect(output).Not.ToBeEditableAsync();
        await Expect(work).ToBeEditableAsync();
        await Expect(form.GetByText(ownedNote, new() { Exact = true })).ToHaveCountAsync(2);

        await form.GetByRole(AriaRole.Heading, new() { Name = "Media manager", Exact = true }).ClickAsync();
        await form.GetByTestId("library-unlink").ClickAsync();
        await Expect(watched).ToBeEditableAsync();
        await Expect(output).ToBeEditableAsync();
        await Expect(form.GetByText(ownedNote, new() { Exact = true })).ToHaveCountAsync(0);

        await page.GetByTestId("processing-library-save").ClickAsync();
        await Expect(form).ToHaveCountAsync(0);
        var saved = await arranged.Client.GetAsync($"{Libraries}/{arranged.MoviesId}");
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        Assert.Null(saved.Fields["folders_synced_from_connection_id"]);
    }

    [E2EFact]
    public async Task A_workflow_linked_to_Deluno_offers_no_way_to_delete_a_rejected_file()
    {
        using var rig = new TemporaryFolder();
        await using var arranged = await LinkedDeluno.StartAsync(BaseUrl, rig);
        var page = await NewPageAsync();

        await OpenWorkflowEditorAsync(page, arranged.MoviesId);
        var form = page.GetByTestId("processing-library-form");
        await form.GetByRole(AriaRole.Heading, new() { Name = "Intake rules", Exact = true }).ClickAsync();
        var action = form.GetByRole(AriaRole.Combobox, new() { Name = "When a file is rejected" });

        await Expect(action).ToBeDisabledAsync();
        await Expect(action).ToHaveValueAsync("leave");
        await Expect(form.GetByTestId("rejected_file_action-locked")).ToContainTextAsync(
            $"Linked to {arranged.Name}: the original stays with your download client, which may still be seeding.");
    }

    private async Task OpenWorkflowEditorAsync(IPage page, long workflowId)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Workflows", "File paths");
        await page.GetByTestId($"processing-library-{workflowId}").GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
    }
}
