using System.Text.Json;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup › Workflows › File paths, the editor: the choices that decide what happens to a file Weir turns away or gives up on, each with
/// the words that say what it does, and the offer to hand a release back that only a manager that can take one allows.
/// </summary>
public sealed class WorkflowEditorPoliciesTests(E2EServer server) : E2ETestBase(server)
{
    private const string Handback = "Your media manager still gets the file, exactly as it arrived. The original stays in the watched folder.";
    private const string Hold = "The file stays with Weir and will not reach your media manager until you deal with it.";
    private const string Reject = "Weir tells your media manager the release is bad and removes the download once the media manager accepts, so it can find a different one. If that cannot be done safely, the original is passed through unchanged instead.";

    [E2EFact]
    public async Task A_Weir_only_workflow_chooses_what_to_do_with_a_rejected_file_and_when_retries_run_out_and_cannot_choose_Reject()
    {
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var form = await WorkflowEditor.OpenNewAsync(page);
        await WorkflowEditor.Text(form, "Name").FillAsync("E2E policies");

        // A rejected file is left in place unless the workflow is told to delete it; nothing locks the choice for a Weir-only workflow.
        await WorkflowEditor.OpenGroupAsync(form, "Intake rules");
        var rejected = WorkflowEditor.Choice(form, "When a file is rejected");
        await Expect(rejected).ToBeEnabledAsync();
        await Expect(rejected).ToHaveValueAsync("leave");
        await Expect(form.GetByTestId("rejected_file_action-locked")).ToHaveCountAsync(0);
        await Expect(form).ToContainTextAsync("Applies to a file this workflow's own size, date or path settings turn away, and to a file its rules find nothing in to keep. Weir never deletes a populated parent folder here.");
        Assert.Equal(["Leave the file in place", "Delete only the rejected file"], await rejected.Locator("option").AllInnerTextsAsync());

        // When retries run out: each choice has its own words, and Reject is on the list but cannot be chosen with no manager to take it.
        await WorkflowEditor.OpenGroupAsync(form, "When a file fails");
        var runOut = WorkflowEditor.Choice(form, "When retries run out");
        var support = form.GetByTestId("reject-support");
        await Expect(runOut).ToHaveValueAsync("pass_through");
        await Expect(form).ToContainTextAsync(Handback);
        await Expect(support).ToHaveTextAsync("Reject: Link a media manager to this workflow first. Rejecting needs a manager that can find a different release.");
        await Expect(runOut.Locator("option[value=reject]")).ToHaveAttributeAsync("disabled", string.Empty);
        await runOut.SelectOptionAsync("hold");
        await Expect(form).ToContainTextAsync(Hold);
        await Expect(form).Not.ToContainTextAsync(Handback);

    }

    [E2EFact]
    public async Task A_workflow_linked_to_a_manager_that_can_take_a_release_back_offers_Reject_and_keeps_the_choice_when_saved()
    {
        using var folders = new TemporaryFolder();
        var completed = Directory.CreateDirectory(Path.Join(folders.Path, "completed")).FullName;
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var rig = await SonarrFedWorkflow.StartAsync(BaseUrl, "E2E Reject", completed, watched, output);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        await RecordRejectSupportTextAsync(page);
        var form = await WorkflowEditor.OpenEditAsync(page, rig.WorkflowId);
        await WorkflowEditor.OpenGroupAsync(form, "When a file fails");
        var runOut = WorkflowEditor.Choice(form, "When retries run out");
        var reject = runOut.Locator("option[value=reject]");

        // Weir says it is checking until the manager has answered.
        await Expect(form.GetByTestId("reject-support")).ToContainTextAsync("Reject: ");
        var seen = await page.EvaluateAsync<JsonElement>("() => window.__rejectSupportTexts");
        var texts = seen.EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Equal("Checking whether Reject is available…", texts[0]);
        Assert.StartsWith("Reject: ", texts[^1], StringComparison.Ordinal);
        await Expect(reject).Not.ToHaveAttributeAsync("disabled", string.Empty);
        await runOut.SelectOptionAsync("reject");
        await Expect(form).ToContainTextAsync(Reject);
        await WorkflowEditor.Save(page).ClickAsync();
        await Expect(form).ToHaveCountAsync(0);
        Assert.Equal("reject", (string)(await WorkflowEditor.ReadAsync(rig.Admin, rig.WorkflowId))["failure_policy"]!);

        // A workflow already on Reject keeps the choice on its list when it is opened.
        var reopened = await WorkflowEditor.OpenEditAsync(page, rig.WorkflowId);
        await WorkflowEditor.OpenGroupAsync(reopened, "When a file fails");
        await Expect(WorkflowEditor.Choice(reopened, "When retries run out")).ToHaveValueAsync("reject");
        await Expect(reopened).ToContainTextAsync(Reject);
    }

    /// <summary>Records every text the "Reject is available" line shows, from the moment this runs, so a text that lasts a moment is not missed.</summary>
    private static async Task RecordRejectSupportTextAsync(IPage page) =>
        await page.EvaluateAsync(
            """
            () => {
              const seen = (window.__rejectSupportTexts = []);
              const look = () => {
                const element = document.querySelector('[data-testid="reject-support"]');
                const text = element ? element.textContent.trim() : '';
                if (text && seen[seen.length - 1] !== text) seen.push(text);
              };
              new MutationObserver(look).observe(document.body, { subtree: true, childList: true, characterData: true });
              look();
            }
            """);
}
