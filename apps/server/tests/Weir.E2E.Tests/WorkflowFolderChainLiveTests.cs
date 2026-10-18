using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Workflows › File paths, the editor's Folder chain: while it is open it follows Weir's own look at the folders, with no Check again and no reload.</summary>
public sealed class WorkflowFolderChainLiveTests(E2EServer server) : E2ETestBase(server)
{
    // The server looks at the folders every 15 s while a browser watches, and this test runs it ten times as often. The page reads the
    // answer when it is told; this is the most it is given, and only a hung server reaches it.
    private const float FolderCheckMs = 20_000;
    private const int QuickTasks = 10;

    [E2EFact]
    public async Task An_open_folder_chain_turns_to_needing_attention_when_a_folder_goes_and_back_to_ready_when_it_returns()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var own = await E2EServer.StartWithQuickTasksAsync(QuickTasks);
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, baseUrl, "E2E chain films", watched, output);
        try
        {
            await WorkflowEditor.OpenFilePathsAsync(page, baseUrl);
            var form = await WorkflowEditor.OpenEditAsync(page, id);
            var weirsOwn = form.GetByTestId("library-folder-chain").Locator("section[aria-label=\"Weir's own folders\"] [data-status]").First;
            await Expect(weirsOwn).ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");

            // Unreachable, the way a share that drops off the network is: the folder is simply not there.
            Directory.Delete(watched);
            await Expect(weirsOwn).Not.ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            await Expect(form.GetByTestId("library-folder-chain")).ToContainTextAsync(watched);

            Directory.CreateDirectory(watched);
            await Expect(weirsOwn).ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            // A job of the workflow cannot finish while its folder is gone, and Weir will not remove a workflow with a job left.
            Directory.CreateDirectory(watched);
            await WeirWorkflowApi.DeleteAsync(page.Context, baseUrl, id);
        }
    }
}
