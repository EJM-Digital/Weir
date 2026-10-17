using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A Dashboard link that names a workflow narrows Live to it. A link that names one that is switched off, deleted or not a whole number
/// shows everything instead, and the picker says so; while the list of workflows loads the link's own choice stands, so the page never
/// shows every workflow's files first. One movie is being cleaned in the Movies workflow, so what Live counts as working says which
/// workflows it is showing.
/// </summary>
public sealed class DashboardWorkflowLinkTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const string Film = "Harbour Lights 2024.mkv";
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";

    // Notes whether the film is ever on the page, from the first thing the page draws, so a page that showed every workflow before it
    // knew which one the link meant would be caught in the act.
    private const string WatchForFilm = """
        window.__filmSeen = false;
        new MutationObserver(() => {
          if (document.documentElement.textContent.includes('Harbour Lights')) window.__filmSeen = true;
        }).observe(document, { childList: true, subtree: true, characterData: true });
        """;

    [E2EFact]
    public async Task A_link_naming_a_workflow_that_is_switched_off_deleted_or_not_a_number_shows_everything()
    {
        await using var rig = await ProcessingRig.StartAsync();
        using var folders = new TemporaryFolder();
        var switchedOff = await CreateWorkflowAsync(rig, folders, "E2E switched off");
        var deleted = await CreateWorkflowAsync(rig, folders, "E2E deleted");
        await SwitchOffAsync(rig, switchedOff);
        await DeleteAsync(rig, deleted);
        var page = await OpenWorkingFilmAsync(rig);

        foreach (var workflow in new[] { switchedOff.ToString(), deleted.ToString(), "abc", "0", "-1", "2abc", "99999999999", string.Empty })
        {
            await page.GotoAsync($"{rig.BaseUrl}/?workflow={workflow}");

            await Expect(page.GetByTestId("workflow-picker")).ToContainTextAsync("All workflows", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
        }
    }

    [E2EFact]
    public async Task A_link_naming_a_workflow_that_is_on_narrows_Live_to_it_and_never_shows_the_others_while_the_list_loads()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenWorkingFilmAsync(rig);
        var tv = await WorkflowIdAsync(rig, "tv");
        var movies = await WorkflowIdAsync(rig, "movie");
        await page.AddInitScriptAsync(WatchForFilm);

        await page.GotoAsync($"{rig.BaseUrl}/?workflow={tv}");

        await Expect(page.GetByTestId("workflow-picker")).ToContainTextAsync("TV", new() { Timeout = PushMs });
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("0", new() { Timeout = PushMs });
        Assert.False(await page.EvaluateAsync<bool>("() => window.__filmSeen"), "The film of another workflow was on the page while the link's workflow was loading.");

        // The same watch sees the film when the link names the workflow it is in, so it is the narrowing that kept it out above.
        await page.GotoAsync($"{rig.BaseUrl}/?workflow={movies}");

        await Expect(page.GetByTestId("workflow-picker")).ToContainTextAsync("Movies", new() { Timeout = PushMs });
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
        Assert.True(await page.EvaluateAsync<bool>("() => window.__filmSeen"));
    }

    /// <summary>A film being cleaned in Movies, with the page signed in and showing it.</summary>
    private async Task<IPage> OpenWorkingFilmAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await rig.HandOffAsync("h-harbour", Film);
        await rig.WaitUntilWorkingAsync("h-harbour");
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
        return page;
    }

    private static async Task<long> WorkflowIdAsync(ProcessingRig rig, string mediaType)
    {
        var listed = await rig.Admin.GetAsync(Libraries);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return (long)listed.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == mediaType)["id"]!;
    }

    private static async Task<long> CreateWorkflowAsync(ProcessingRig rig, TemporaryFolder folders, string name)
    {
        var created = await rig.Admin.PostWithCsrfAsync(Libraries, new JsonObject
        {
            ["name"] = name,
            ["media_type"] = "movie",
            ["watched_folder"] = Directory.CreateDirectory(Path.Join(folders.Path, name, "watched")).FullName,
            ["output_folder"] = Directory.CreateDirectory(Path.Join(folders.Path, name, "output")).FullName,
        });
        Assert.True(created.Status == HttpStatusCode.Created || created.Status == HttpStatusCode.OK, created.ToString());
        return (long)created.Fields["id"]!;
    }

    private static async Task SwitchOffAsync(ProcessingRig rig, long id)
    {
        var path = $"{Libraries}/{id}";
        var read = await rig.Admin.GetAsync(path);
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        var body = LibraryBodies.Unchanged(read.Fields);
        body["enabled"] = false;
        var saved = await rig.Admin.PutWithCsrfAsync(path, body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    private static async Task DeleteAsync(ProcessingRig rig, long id)
    {
        await Poll.UntilAsync(
            async () => (await rig.Admin.DeleteWithCsrfBodyAsync($"{Libraries}/{id}")).Status == HttpStatusCode.NoContent,
            $"workflow {id} to be removed");
    }
}
