using System.Net;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Workflows › File paths, workflows that come from a media manager: unlinking one, importing from a manager, and what the notice says when it fails.</summary>
public sealed class WorkflowManagerLinkTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Unlink_makes_a_workflow_from_a_media_manager_Weir_only_and_says_nothing_else_changed()
    {
        using var rig = new TemporaryFolder();
        await using var arranged = await LinkedDeluno.StartAsync(BaseUrl, rig);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var row = WorkflowEditor.Row(page, arranged.MoviesId);
        var before = await WorkflowEditor.ReadAsync(arranged.Client, arranged.MoviesId);
        await Expect(row.GetByText("Kept in step with it.", new() { Exact = true })).ToBeVisibleAsync();

        await row.GetByRole(AriaRole.Button, new() { Name = "Unlink", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("processing-library-notice")).ToHaveTextAsync("Movies is now a Weir-only workflow. Its folders and settings were not changed.");
        await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Unlink", Exact = true })).ToHaveCountAsync(0);
        await Expect(row.GetByText("Kept in step with it.", new() { Exact = true })).ToHaveCountAsync(0);
        var after = await WorkflowEditor.ReadAsync(arranged.Client, arranged.MoviesId);
        Assert.Null(after["discovered_from_connection_id"]);
        Assert.Equal((string)before["watched_folder"]!, (string)after["watched_folder"]!);
        Assert.Equal((string)before["output_folder"]!, (string)after["output_folder"]!);
    }

    [E2EFact]
    public async Task A_media_manager_that_gives_no_answer_is_named_in_the_notice_when_its_workflows_are_asked_for()
    {
        using var rig = new TemporaryFolder();
        await using var arranged = await LinkedDeluno.StartAsync(BaseUrl, rig);
        arranged.Deluno.Route("GET", "/api/integrations/external/manifest", status: (int)HttpStatusCode.InternalServerError);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var import = page.GetByRole(AriaRole.Region, new() { Name = "Import from a media manager" });
        var notice = page.GetByTestId("processing-library-notice");

        await Expect(import.GetByRole(AriaRole.Button, new() { Name = "Discover workflows" })).ToBeDisabledAsync();
        await import.GetByLabel("Media manager").SelectOptionAsync(new SelectOptionValue { Label = arranged.Name });
        await import.GetByRole(AriaRole.Button, new() { Name = "Discover workflows" }).ClickAsync();
        await Expect(notice).ToHaveTextAsync(
            $"{arranged.Name} did not give Weir the answer it expected when asked what it manages. " +
            $"Check the address points at {arranged.Name} itself, not a page inside it, and that {arranged.Name} is running normally.");
    }
}
