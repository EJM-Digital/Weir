using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Workflows › File paths › Edit › Output safety, "New downloads: after cleaning, delete the original download". On a workflow only Weir
/// feeds the switch is saved as chosen and decides what a pass does with the release folder it cleaned; on a workflow linked to a media
/// manager it shows as off and locked with the reason, keeps what was saved, and the original stays whatever was saved. Each is read back
/// from the server and the watched folder, after a real pass over a fake ffmpeg.
/// </summary>
public sealed class OriginalDownloadSwitchTests(E2EServer server) : E2ETestBase(server)
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string Field = "remove_original_after_success";
    private const string Film = "Linked.Film.mkv";

    private static readonly Regex SwitchName = new("^New downloads: after cleaning, delete the original download");

    [E2EFact]
    public async Task On_a_workflow_only_Weir_feeds_the_switch_is_saved_as_chosen_and_a_pass_removes_or_keeps_the_original_to_match()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var movies = await rig.MoviesWorkflowIdAsync();
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);

        // Off, as the rig left it: a pass keeps the release folder.
        Assert.False(await SavedAsync(rig, movies));
        await rig.DropFileAsync("Kept.Release", FilmOf("Kept.Release"));
        await WaitForPassAsync(rig, "Kept.Release");
        Assert.True(rig.WatchedReleaseExists("Kept.Release"), "With the switch off the release folder must stay.");

        // On: the choice is saved and shown again after a reload, and a pass removes the release folder it cleaned.
        var form = await OpenEditorAsync(page, rig, movies);
        var toggle = Switch(form);
        await Expect(toggle).Not.ToBeCheckedAsync();
        await toggle.CheckAsync();
        await SaveAsync(page, form);
        Assert.True(await SavedAsync(rig, movies));
        form = await OpenEditorAsync(page, rig, movies);
        await Expect(Switch(form)).ToBeCheckedAsync();
        await rig.DropFileAsync("Removed.Release", FilmOf("Removed.Release"));
        await WaitForPassAsync(rig, "Removed.Release");
        Assert.False(rig.WatchedReleaseExists("Removed.Release"), "With the switch on the cleaned release folder must go.");

        // Off again: saved, shown after a reload, and the next release folder stays.
        form = await OpenEditorAsync(page, rig, movies);
        await Switch(form).UncheckAsync();
        await SaveAsync(page, form);
        Assert.False(await SavedAsync(rig, movies));
        form = await OpenEditorAsync(page, rig, movies);
        await Expect(Switch(form)).Not.ToBeCheckedAsync();
        await rig.DropFileAsync("Kept.Again", FilmOf("Kept.Again"));
        await WaitForPassAsync(rig, "Kept.Again");
        Assert.True(rig.WatchedReleaseExists("Kept.Again"), "With the switch off again the release folder must stay.");
    }

    [E2EFact]
    public async Task A_new_workflow_has_the_switch_on()
    {
        await using var rig = await ProcessingRig.StartAsync();
        using var folders = new TemporaryFolder();
        var created = await rig.Admin.PostWithCsrfAsync(Libraries, new JsonObject
        {
            ["name"] = "E2E new workflow",
            ["media_type"] = "movie",
            ["watched_folder"] = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName,
            ["output_folder"] = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName,
        });
        Assert.True(created.Status is HttpStatusCode.Created or HttpStatusCode.OK, created.ToString());
        var id = (long)created.Fields["id"]!;
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);

        var form = await OpenEditorAsync(page, rig, id);

        Assert.True(await SavedAsync(rig, id));
        await Expect(Switch(form)).ToBeCheckedAsync();
    }

    [E2EFact]
    public async Task A_linked_workflow_shows_the_switch_off_and_locked_with_the_reason_keeps_what_was_saved_and_keeps_the_original()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var movies = await rig.MoviesWorkflowIdAsync();
        await SaveSwitchAsync(rig, movies, on: true);
        await rig.LinkMoviesToDelunoAsync();
        var manager = await rig.DelunoNameAsync();
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);

        var form = await OpenEditorAsync(page, rig, movies);

        var toggle = Switch(form);
        await Expect(toggle).Not.ToBeCheckedAsync();
        await Expect(toggle).ToBeDisabledAsync();
        await Expect(form.GetByTestId($"{Field}-locked")).ToContainTextAsync($"Linked to {manager}: the original stays with your download client, which may still be seeding.");
        await SaveAsync(page, form);
        Assert.True(await SavedAsync(rig, movies), "Saving a linked workflow must leave the saved switch as it was.");

        await rig.HandOffAsync("h-linked", Film);
        await rig.WaitUntilWorkingAsync("h-linked");
        rig.ReleasePass(Film);
        await rig.WaitForWorkToEndAsync();
        Assert.True(rig.WatchedFileExists("h-linked", Film), "A linked workflow must leave the original where the manager's download client put it.");
    }

    private static ILocator Switch(ILocator form) => form.GetByRole(AriaRole.Checkbox, new() { NameRegex = SwitchName });

    private static async Task<bool> SavedAsync(ProcessingRig rig, long id)
    {
        var read = await rig.Admin.GetAsync($"{Libraries}/{id}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        return (bool)read.Fields[Field]!;
    }

    private static async Task SaveSwitchAsync(ProcessingRig rig, long id, bool on)
    {
        var read = await rig.Admin.GetAsync($"{Libraries}/{id}");
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        var body = LibraryBodies.Unchanged(read.Fields);
        body[Field] = on;
        var saved = await rig.Admin.PutWithCsrfAsync($"{Libraries}/{id}", body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    /// <summary>The film in a release folder: named for it, so the server lists exactly one file of that name.</summary>
    private static string FilmOf(string releaseFolder) => $"{releaseFolder}.mkv";

    /// <summary>Waits for the pass over the release folder's film to write its output and for Weir to have no work left, which is when the original has been removed or kept.</summary>
    private static async Task WaitForPassAsync(ProcessingRig rig, string releaseFolder)
    {
        await Poll.UntilAsync(() => Task.FromResult(rig.OutputFileExists(releaseFolder, FilmOf(releaseFolder))), $"the pass over {releaseFolder} to write its output");
        await rig.WaitForWorkToEndAsync();
    }

    /// <summary>Opens the workflow's editor on the page, afresh, with Output safety open.</summary>
    private static async Task<ILocator> OpenEditorAsync(IPage page, ProcessingRig rig, long workflow)
    {
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await Navigation.OpenTabAsync(page, "Workflows", "File paths");
        await page.GetByTestId($"processing-library-{workflow}").GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        var form = page.GetByTestId("processing-library-form");
        await form.GetByRole(AriaRole.Heading, new() { Name = "Output safety", Exact = true }).ClickAsync();
        await Expect(Switch(form)).ToBeVisibleAsync();
        return form;
    }

    private static async Task SaveAsync(IPage page, ILocator form)
    {
        await page.GetByTestId("processing-library-save").ClickAsync();
        await Expect(form).ToHaveCountAsync(0);
    }
}
