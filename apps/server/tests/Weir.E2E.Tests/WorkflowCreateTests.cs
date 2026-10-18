using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup › Workflows › File paths, Add workflow: the button beside the tabs, the choice of where the workflow comes from, and the editor
/// that saves a new Weir-only workflow with the defaults Weir starts one with.
/// </summary>
public sealed class WorkflowCreateTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E created with defaults";

    [E2EFact]
    public async Task Add_workflow_sits_beside_the_tabs_and_goes_when_another_tab_opens()
    {
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var add = page.GetByTestId("processing-library-add");
        
        await Expect(add).ToBeVisibleAsync();
        await AssertBesideTheTabsAsync(page, add);

        await page.GetByRole(AriaRole.Tab, new() { Name = "Schedule", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("processing-schedules-section")).ToBeVisibleAsync();
        await Expect(add).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Tab, new() { Name = "File paths", Exact = true }).ClickAsync();
        await Expect(add).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Add_workflow_stays_at_the_right_of_the_tab_row_in_a_narrow_window()
    {
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        await page.SetViewportSizeAsync(800, 900);

        await AssertBesideTheTabsAsync(page, page.GetByTestId("processing-library-add"));
    }

    private static async Task AssertBesideTheTabsAsync(IPage page, ILocator add)
    {
        await Expect(add).ToBeVisibleAsync();
        var addBox = (await add.BoundingBoxAsync())!;
        var tabBox = (await page.GetByRole(AriaRole.Tab, new() { Name = "File paths", Exact = true }).BoundingBoxAsync())!;
        Assert.True(Math.Abs(addBox.Y + addBox.Height / 2 - (tabBox.Y + tabBox.Height / 2)) < tabBox.Height / 2, "Add workflow is not on the line of the tabs.");
        Assert.True(addBox.X > tabBox.X + tabBox.Width, "Add workflow is not at the right of the tabs.");
    }

    [E2EFact]
    public async Task The_choice_offers_Local_folders_and_says_a_media_manager_is_not_connected_yet_and_Cancel_adds_nothing()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var before = await SetupSnapshot.TakeAsync(admin);
        var log = RequestLog.Watch(page);
        var mark = log.Mark();

        await page.GetByTestId("processing-library-add").ClickAsync();
        var choice = page.GetByTestId("add-workflow-choice");
        await Expect(choice.GetByRole(AriaRole.Radio, new() { Name = "Local folders" })).ToBeCheckedAsync();
        var fromManager = choice.GetByRole(AriaRole.Radio, new() { Name = "From a media manager" });
        await Expect(fromManager).ToBeDisabledAsync();
        await Expect(choice).ToContainTextAsync("Nothing is connected yet. Connect Deluno, Sonarr or Radarr first.");
        await choice.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(choice).ToHaveCountAsync(0);
        await Expect(WorkflowEditor.Form(page)).ToHaveCountAsync(0);

        log.AssertNothingSentSince(mark);
        Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
    }

    [E2EFact]
    public async Task A_name_alone_saves_a_workflow_with_every_default_and_the_editor_closes()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);

        var form = await WorkflowEditor.OpenNewAsync(page);
        await Expect(WorkflowEditor.Save(page)).ToBeDisabledAsync();
        await WorkflowEditor.Text(form, "Name").FillAsync($"  {Name}  ");
        await Expect(WorkflowEditor.Save(page)).ToBeEnabledAsync();
        var mark = log.Mark();
        await WorkflowEditor.Save(page).ClickAsync();
        await Expect(form).ToHaveCountAsync(0);

        var id = await WorkflowEditor.IdOfAsync(admin, Name);
        try
        {
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {WeirClient.Api}/processing/libraries", $"{sent.Method} {sent.PathAndQuery}");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
            body.Remove("csrf_token");
            Assert.True(JsonNode.DeepEquals(WorkflowBodies.Default(Name), body), $"The page sent {body.ToJsonString()}");

            // The server holds what was sent (a workflow with no profile chosen is given its kind's default one), and the list shows the workflow with the name trimmed.
            var stored = await WorkflowEditor.ReadAsync(admin, id);
            foreach (var (field, expected) in WorkflowBodies.Default(Name).Where(field => field.Key != "rule_set_id"))
            {
                Assert.True(JsonNode.DeepEquals(expected, stored[field]), $"{field}: the server holds {stored[field]?.ToJsonString()}, not {expected?.ToJsonString()}");
            }

            await Expect(WorkflowEditor.Row(page, id)).ToContainTextAsync(Name);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }
}
