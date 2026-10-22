using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// "Remove from list" on a failed film whose file is still in the watched folder asks what to do with it. The dialog words the choices
/// for who does the deleting (Weir alone, the manager that can take the release back, or no one when the workflow is linked to a manager
/// that cannot), nothing is chosen until the person chooses, and each choice sends exactly its own resolution in one request. The
/// file on disk, the row Activity lists and Weir's answer are read back after each.
/// </summary>
public sealed class RemoveFromActivityChoicesTests(E2EServer server) : E2ETestBase(server)
{
    private const float PassMs = 180_000;
    private const float PushMs = 10_000;
    private const string FilesRoute = $"{WeirClient.Api}/processing/files";
    private const string Capability = "processor-reject-regrab";

    [E2EFact]
    public async Task With_no_manager_the_dialog_says_Weir_alone_deletes_and_each_choice_sends_exactly_its_resolution()
    {
        await using var rig = await ProcessingRig.StartAsync(holdFailures: true);
        const string deleted = "Deleted.1922.mkv";
        const string kept = "Kept.1922.mkv";
        const string retried = "Retried.1922.mkv";
        foreach (var film in new[] { deleted, kept, retried })
        {
            await rig.DropFileAsync(Release(film), film, failFirstPass: true);
        }

        var (page, log) = await OpenActivityAsync(rig);
        foreach (var film in new[] { deleted, kept, retried })
        {
            await Expect(RowOf(page, film)).ToContainTextAsync("Failed", new() { Timeout = PassMs });
        }

        // Delete: the wording names Weir alone, and Remove waits for a choice.
        var dialog = await OpenRemoveDialogAsync(page, deleted);
        await Expect(Choice(dialog, "delete")).ToContainTextAsync("Delete the file");
        await Expect(Choice(dialog, "delete")).ToContainTextAsync("Weir alone: the file is deleted.");
        await Expect(Choice(dialog, "keep")).ToContainTextAsync("Weir leaves it until it changes.");
        await Expect(Choice(dialog, "keep")).Not.ToContainTextAsync("told");
        await Expect(ConfirmButton(dialog)).ToBeDisabledAsync();
        await Expect(dialog.GetByRole(AriaRole.Radio, new() { Checked = true })).ToHaveCountAsync(0);
        await Expect(Radio(dialog, "delete")).ToBeFocusedAsync();
        var listed = await ListedAsync(rig, deleted);
        await Radio(dialog, "delete").CheckAsync();
        var mark = log.Mark();
        await ConfirmButton(dialog).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
        AssertOneRemoval(log, mark, listed, "delete");
        Assert.False(rig.WatchedFileExists(Release(deleted), deleted), "Delete must remove the file from the watched folder.");
        Assert.Null(await rig.ListedFileAsync(deleted));
        await Expect(RowOf(page, deleted)).ToHaveCountAsync(0);

        // Keep: the file stays, Weir stops looking at it, and the row goes.
        dialog = await OpenRemoveDialogAsync(page, kept);
        listed = await ListedAsync(rig, kept);
        await Radio(dialog, "keep").CheckAsync();
        mark = log.Mark();
        await ConfirmButton(dialog).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
        AssertOneRemoval(log, mark, listed, "keep");
        Assert.True(rig.WatchedFileExists(Release(kept), kept), "Keep must leave the file in the watched folder.");
        Assert.Null(await rig.ListedFileAsync(kept));
        var keptFiles = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/kept-files");
        var keptFile = Assert.Single(keptFiles.Fields["files"]!.AsArray().OfType<JsonObject>());
        Assert.Equal($"{Release(kept)}/{kept}", (string?)keptFile["relative_path"]);

        // Try it again: the file stays and is cleaned on the next pass.
        dialog = await OpenRemoveDialogAsync(page, retried);
        listed = await ListedAsync(rig, retried);
        await Radio(dialog, "retry").CheckAsync();
        mark = log.Mark();
        await ConfirmButton(dialog).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
        AssertOneRemoval(log, mark, listed, "retry");
        Assert.True(rig.WatchedFileExists(Release(retried), retried), "Try it again must leave the file where it is.");
        // The row leaves Needs you because the next pass finishes the film.
        await Poll.UntilAsync(async () => (string?)(await rig.ListedFileAsync(retried))?["status"] == "processed", "the next pass to clean the film", TimeSpan.FromMilliseconds(PassMs));
        await Expect(RowOf(page, retried)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task A_manager_that_takes_releases_back_is_named_and_Delete_asks_it_for_another_copy()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true, delunoCapabilities: [Capability]);
        const string film = "Nosferatu.1922.mkv";
        var manager = await rig.DelunoNameAsync();
        var (page, log) = await OpenActivityAsync(rig);
        await rig.HandOffReleaseAsync("h-nosferatu", film, "Gallery.mkv", failFirstPass: true);
        await Expect(RowOf(page, film)).ToContainTextAsync("Failed", new() { Timeout = PassMs });

        var dialog = await OpenRemoveDialogAsync(page, film);

        await Expect(Choice(dialog, "delete")).ToContainTextAsync($"Delete the download and ask {manager} for another copy");
        await Expect(Choice(dialog, "delete")).ToContainTextAsync($"{manager} removes it, blocks this release and searches again.");
        await Expect(Choice(dialog, "keep")).ToContainTextAsync($"{manager} is told it won't be imported.");
        await Expect(Radio(dialog, "delete")).ToBeEnabledAsync();
        var listed = await ListedAsync(rig, film);
        var reportsBefore = rig.ReportsSentToDeluno;
        await Radio(dialog, "delete").CheckAsync();
        var mark = log.Mark();

        await ConfirmButton(dialog).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
        AssertOneRemoval(log, mark, listed, "delete");
        Assert.True(rig.ReportsSentToDeluno > reportsBefore, "Delete must ask the manager to take the release back.");
        Assert.Null(await rig.ListedFileAsync(film));
    }

    [E2EFact]
    public async Task A_workflow_linked_to_a_manager_that_cannot_take_it_back_disables_Delete_with_the_reason_and_no_key_chooses_it()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        const string film = "Nosferatu.1922.mkv";
        await rig.LinkMoviesToDelunoAsync();
        var (page, log) = await OpenActivityAsync(rig);
        await rig.HandOffReleaseAsync("h-nosferatu", film, "Gallery.mkv", failFirstPass: true);
        await Expect(RowOf(page, film)).ToContainTextAsync("Failed", new() { Timeout = PassMs });
        var before = await ListedAsync(rig, film);

        var dialog = await OpenRemoveDialogAsync(page, film);

        var delete = Radio(dialog, "delete");
        await Expect(delete).ToBeDisabledAsync();
        await Expect(Choice(dialog, "delete")).ToContainTextAsync("remove the download from your download client");
        await Expect(Radio(dialog, "keep")).ToBeFocusedAsync();
        var mark = log.Mark();

        // Walking the choices with the arrow keys skips the one that cannot be chosen, and Enter with nothing chosen does nothing.
        await page.Keyboard.PressAsync("Enter");
        await Expect(dialog).ToBeVisibleAsync();
        for (var press = 0; press < 5; press++)
        {
            await page.Keyboard.PressAsync("ArrowDown");
            await Expect(delete).Not.ToBeCheckedAsync();
        }

        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        log.AssertNothingSentSince(mark);
        Assert.Equal(before.ToJsonString(), (await ListedAsync(rig, film)).ToJsonString());
        Assert.True(rig.WatchedFileExists("h-nosferatu", film), "The download must stay where the manager put it.");
    }

    private static string Release(string film) => $"r-{film}";

    private static ILocator RowOf(IPage page, string film) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = film });

    private static ILocator Choice(ILocator dialog, string resolution) => dialog.GetByTestId($"activity-remove-dialog-choice-{resolution}");

    private static ILocator Radio(ILocator dialog, string resolution) => Choice(dialog, resolution).GetByRole(AriaRole.Radio);

    private static ILocator ConfirmButton(ILocator dialog) => dialog.GetByTestId("activity-remove-dialog-confirm");

    private async Task<(IPage Page, RequestLog Log)> OpenActivityAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        // The side menu's Activity link goes to Needs you only while its badge shows, and the badge arrives after the page does, so
        // the link alone leaves it to chance which view is open. The retried film leaves Needs you but is listed in All.
        await page.GotoAsync($"{rig.BaseUrl}/activity?show=attention");
        return (page, log);
    }

    private static async Task<ILocator> OpenRemoveDialogAsync(IPage page, string film)
    {
        await RowOf(page, film).GetByRole(AriaRole.Button).ClickAsync();
        await Expect(RowOf(page, film)).ToHaveAttributeAsync("aria-current", "true");
        await page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button, new() { Name = "Remove from list", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("activity-remove-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = PushMs });
        await Expect(dialog).ToContainTextAsync(new Regex($"Remove \"[^\"]*{Regex.Escape(film)}\" from Activity[?]"));
        return dialog;
    }

    /// <summary>The film's row as the server lists it.</summary>
    private static async Task<JsonObject> ListedAsync(ProcessingRig rig, string film) =>
        await rig.ListedFileAsync(film) ?? throw new InvalidOperationException($"Activity does not list {film}.");

    /// <summary>The page sent one request since <paramref name="mark"/>: the removal of this file, carrying the choice and the page's CSRF token.</summary>
    private static void AssertOneRemoval(RequestLog log, int mark, JsonObject listed, string resolution)
    {
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal($"{FilesRoute}/{(long)listed["id"]!}", sent.PathAndQuery);
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.Equal(resolution, (string?)body["resolution"]);
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The removal carries the page's CSRF token.");
    }
}
