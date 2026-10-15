using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup › Workflows › File paths, Remove: the dialog that asks before a workflow goes. Keep it, Escape and a click outside the dialog
/// never remove the workflow and never send a request; only Remove workflow does.
/// </summary>
public sealed class RemoveWorkflowDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const string DialogId = "processing-library-remove-confirm";
    private const string Name = "E2E workflow to keep";

    // How many jobs there are depends on whether the watcher is looking at the folders at that moment.
    private static Regex StillHasWork() => new($@"{Name} still has \d+ jobs? queued or running\.");

    [E2EFact]
    public async Task Keep_it_Escape_and_a_click_outside_the_dialog_leave_the_workflow_alone_and_send_nothing()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await CreateWorkflowAsync(page, folders);
        try
        {
            var before = await SetupSnapshot.TakeAsync(admin);
            var (row, dialog, sent) = await OpenFilePathsAsync(page, id);

            await OpenDialogAsync(page, id, dialog);
            await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(row).ToContainTextAsync(Name);

            await OpenDialogAsync(page, id, dialog);
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(row).ToContainTextAsync(Name);

            // The dimmed area around the card does nothing: the question stays until it is answered.
            await OpenDialogAsync(page, id, dialog);
            await Dialogs.ClickOutsideAsync(dialog);
            await Expect(dialog).ToBeVisibleAsync();
            await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(row).ToContainTextAsync(Name);

            Assert.Empty(await sent.SoFarAsync());
            Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task Removing_a_workflow_that_still_has_work_queued_shows_why_and_cancelling_clears_it()
    {
        using var folders = new TemporaryFolder();
        var libraryFolder = Directory.CreateDirectory(Path.Join(folders.Path, "library")).FullName;
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var context = page.Context;
        var id = await CreateWorkflowAsync(page, folders);
        try
        {
            await WeirWorkflowApi.SetLibraryFoldersAsync(context, BaseUrl, id, libraryFolder);
            // Paused, the scan waits in the queue, and a workflow with work queued cannot be removed.
            await WeirApi.SetPausedAsync(context, BaseUrl, paused: true);
            await WeirWorkflowApi.StartLibraryScanAsync(context, BaseUrl, id);
            await WorkflowJobs.WaitUntilBusyAsync(admin, id);
            var before = await SetupSnapshot.TakeAsync(admin);
            var (row, dialog, sent) = await OpenFilePathsAsync(page, id);
            var reason = dialog.GetByRole(AriaRole.Alert);
            var removal = $"DELETE {WeirClient.Api}/processing/libraries/{id}";

            await OpenDialogAsync(page, id, dialog);
            await page.GetByTestId($"{DialogId}-confirm").ClickAsync();
            await Expect(reason).ToContainTextAsync(StillHasWork());
            await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await OpenDialogAsync(page, id, dialog);
            await Expect(reason).ToHaveCountAsync(0);

            await page.GetByTestId($"{DialogId}-confirm").ClickAsync();
            await Expect(reason).ToContainTextAsync(StillHasWork());
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await OpenDialogAsync(page, id, dialog);
            await Expect(reason).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);

            // Only the two attempts that were confirmed reached the server, and it refused both.
            Assert.Equal([removal, removal], await sent.SoFarAsync());
            await Expect(row).ToContainTextAsync(Name);
            Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
        }
        finally
        {
            await WeirApi.SetPausedAsync(context, BaseUrl, paused: false);
            await WeirWorkflowApi.DeleteAsync(context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task Remove_workflow_sends_one_request_and_the_workflow_goes()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await CreateWorkflowAsync(page, folders);
        var (row, dialog, sent) = await OpenFilePathsAsync(page, id);

        await OpenDialogAsync(page, id, dialog);
        await ConfirmUntilRemovedAsync(page, id);

        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(row).ToHaveCountAsync(0);
        // Weir refuses while it is looking at the workflow's folders, so the person may have had to confirm again: every request was this removal.
        Assert.All(await sent.SoFarAsync(), request => Assert.Equal($"DELETE {WeirClient.Api}/processing/libraries/{id}", request));
        var gone = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    /// <summary>
    /// Confirms the removal, and confirms again each time Weir says it cannot yet because it is looking at the workflow's folders, until
    /// the dialog closes.
    /// </summary>
    private static async Task ConfirmUntilRemovedAsync(IPage page, long id)
    {
        await Poll.UntilAsync(
            async () =>
            {
                var answer = await page.RunAndWaitForResponseAsync(
                    () => page.GetByTestId($"{DialogId}-confirm").ClickAsync(),
                    response => response.Request.Method == "DELETE" && response.Url.EndsWith($"/processing/libraries/{id}", StringComparison.Ordinal));
                return answer.Status == (int)HttpStatusCode.NoContent;
            },
            "Weir to remove the workflow");
    }

    private async Task<long> CreateWorkflowAsync(IPage page, TemporaryFolder folders) =>
        await WeirWorkflowApi.CreateAsync(
            page.Context,
            BaseUrl,
            Name,
            Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName,
            Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName);

    /// <summary>Loads the page afresh so it lists the workflow, opens File paths, and starts recording what the page sends.</summary>
    private async Task<(ILocator Row, ILocator Dialog, ChangesSent Sent)> OpenFilePathsAsync(IPage page, long id)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Workflows", "File paths");
        var row = page.GetByTestId($"processing-library-{id}");
        await Expect(row).ToContainTextAsync(Name);
        return (row, page.GetByTestId(DialogId), ChangesSent.Watch(page));
    }

    private static async Task OpenDialogAsync(IPage page, long id, ILocator dialog)
    {
        await page.GetByTestId($"processing-library-remove-{id}").ClickAsync();
        await Expect(dialog).ToContainTextAsync($"Remove {Name}?");
        await Expect(page.GetByTestId($"{DialogId}-cancel")).ToBeFocusedAsync();
    }
}
