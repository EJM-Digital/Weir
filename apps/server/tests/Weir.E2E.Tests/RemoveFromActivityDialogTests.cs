using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// "Remove from list" on a failed file whose download is still in the watched folder asks what to do with it, and one of the choices
/// deletes it. Leaving the dialog any way but Remove (Cancel, Escape) must send nothing, and clicking beside the dialog must leave it
/// open and send nothing; the server's own answer and the file on disk are read back each time, so the page cannot hide a change.
/// </summary>
public sealed class RemoveFromActivityDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const float PassMs = 180_000;
    private const float PushMs = 10_000;
    private const string Release = "h-nosferatu";
    private const string Film = "Nosferatu.1922.mkv";
    private const string Extra = "Gallery.mkv";

    [E2EFact]
    public async Task Cancel_after_choosing_Delete_sends_nothing_and_leaves_the_failed_file_and_its_row_as_they_were()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        var (page, log, before) = await OpenFailedFileAsync(rig);
        var dialog = await OpenRemoveDialogAsync(page);
        await dialog.GetByRole(AriaRole.Radio, new() { NameRegex = new("^Delete the file") }).CheckAsync();
        var mark = log.Mark();

        await dialog.GetByTestId("activity-remove-dialog-cancel").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingChangedAsync(rig, log, mark, before);
    }

    [E2EFact]
    public async Task Escape_after_choosing_Delete_sends_nothing_and_leaves_the_failed_file_and_its_row_as_they_were()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        var (page, log, before) = await OpenFailedFileAsync(rig);
        var dialog = await OpenRemoveDialogAsync(page);
        await dialog.GetByRole(AriaRole.Radio, new() { NameRegex = new("^Delete the file") }).CheckAsync();
        var mark = log.Mark();

        await page.Keyboard.PressAsync("Escape");

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingChangedAsync(rig, log, mark, before);
    }

    [E2EFact]
    public async Task A_click_beside_the_dialog_leaves_it_open_with_its_choice_and_sends_nothing()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        var (page, log, before) = await OpenFailedFileAsync(rig);
        var dialog = await OpenRemoveDialogAsync(page);
        var delete = dialog.GetByRole(AriaRole.Radio, new() { NameRegex = new("^Delete the file") });
        await delete.CheckAsync();
        var mark = log.Mark();

        await page.Mouse.ClickAsync(2, 2);

        await Expect(dialog).ToBeVisibleAsync();
        await Expect(delete).ToBeCheckedAsync();
        await AssertNothingChangedAsync(rig, log, mark, before);
        await dialog.GetByTestId("activity-remove-dialog-cancel").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingChangedAsync(rig, log, mark, before);
    }

    [E2EFact]
    public async Task Remove_just_from_the_list_sends_one_request_and_forgets_the_row_but_not_the_file()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        var (page, log, _) = await OpenFailedFileAsync(rig);
        var dialog = await OpenRemoveDialogAsync(page);
        var mark = log.Mark();
        await dialog.GetByRole(AriaRole.Radio, new() { NameRegex = new("^Just remove it from the list") }).CheckAsync();

        await dialog.GetByTestId("activity-remove-dialog-confirm").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("DELETE", sent.Method);
        Assert.Matches(@"^/api/v1/processing/files/\d+$", sent.PathAndQuery);
        Assert.Equal("remove", (string?)JsonNode.Parse(sent.Body!)!["resolution"]);
        Assert.Null(await rig.ListedFileAsync(Film));
        Assert.True(rig.WatchedFileExists(Release, Film), "Removing the row must leave the file in the watched folder.");
    }

    /// <summary>A film that failed its pass and is held as failed, selected on the Activity page; returns the page, its log and the film's row as the server lists it.</summary>
    private async Task<(IPage Page, RequestLog Log, JsonObject Before)> OpenFailedFileAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await Navigation.OpenSidebarAsync(page, "Activity");
        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = Film });
        await rig.HandOffReleaseAsync(Release, Film, Extra, failFirstPass: true);
        await Expect(row).ToContainTextAsync("Failed", new() { Timeout = PassMs });
        await row.ClickAsync();
        var before = await rig.ListedFileAsync(Film);
        Assert.NotNull(before);
        Assert.True(rig.WatchedFileExists(Release, Film));
        return (page, log, before);
    }

    private static async Task<ILocator> OpenRemoveDialogAsync(IPage page)
    {
        await page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button, new() { Name = "Remove from list", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("activity-remove-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = PushMs });
        return dialog;
    }

    /// <summary>The page sent nothing since <paramref name="mark"/>, and the server and the disk still hold what they held before the dialog opened.</summary>
    private static async Task AssertNothingChangedAsync(ProcessingRig rig, RequestLog log, int mark, JsonObject before)
    {
        var after = await rig.ListedFileAsync(Film);
        Assert.Empty(log.Since(mark));
        Assert.NotNull(after);
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
        Assert.True(rig.WatchedFileExists(Release, Film), "The failed file must still be in the watched folder.");
    }
}
