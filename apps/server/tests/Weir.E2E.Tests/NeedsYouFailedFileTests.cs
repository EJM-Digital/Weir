using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Dashboard's Needs you panel for a file whose pass failed: it appears with no reload, "Try again" asks the server once and the file
/// leaves when the next pass cleans it, and "Remove…" asks what to do with the file first, unless there is nothing to choose.
/// </summary>
public sealed class NeedsYouFailedFileTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float PassMs = 60_000;
    private const string FilesRoute = $"{WeirClient.Api}/processing/files";

    private async Task<(IPage Page, RequestLog Log)> OpenDashboardAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await Expect(NeedsYouPanel.Of(page)).ToContainTextAsync("Nothing needs you.");
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        return (page, log);
    }

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    [E2EFact]
    public async Task A_failed_file_joins_Needs_you_and_Try_again_asks_the_server_once_and_the_cleaned_file_leaves_with_no_reload()
    {
        await using var rig = await ProcessingRig.StartAsync(holdFailures: true);
        await rig.WithEvidenceAsync(async () =>
        {
            const string film = "Zulu Dawn 1964.mkv";
            var (page, log) = await OpenDashboardAsync(rig);

            await rig.DropFileAsync("r-zulu", film, failFirstPass: true);

            var row = NeedsYouPanel.RowOf(page, "Zulu Dawn");
            await Expect(row).ToBeVisibleAsync(new() { Timeout = PassMs });
            await Expect(NeedsYouPanel.Of(page)).ToContainTextAsync("1 to look at");
            var listed = await rig.ListedFileAsync(film);
            Assert.Equal("processing_failed", (string)listed!["status"]!);
            var mark = log.Mark();

            await row.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();

            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync("Queued again.", new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.Equal($"{FilesRoute}/{(long)listed["id"]!}/requeue", sent.PathAndQuery);
            await Expect(row).ToHaveCountAsync(0, new() { Timeout = PassMs });
            await Expect(NeedsYouPanel.Of(page)).ToContainTextAsync("Nothing needs you.");
            await Poll.UntilAsync(async () => (string?)(await rig.ListedFileAsync(film))?["status"] == "processed", "the next pass to clean the film", TimeSpan.FromMilliseconds(PassMs));
            await AssertNotReloadedAsync(page);
        });
    }

    [E2EFact]
    public async Task Remove_asks_what_to_do_with_the_file_Cancel_and_Escape_change_nothing_and_a_choice_removes_the_row_with_the_servers_notice()
    {
        await using var rig = await ProcessingRig.StartAsync(holdFailures: true);
        await rig.WithEvidenceAsync(async () =>
        {
            const string film = "Quatermass 1955.mkv";
            var (page, log) = await OpenDashboardAsync(rig);
            await rig.DropFileAsync("r-quatermass", film, failFirstPass: true);
            var row = NeedsYouPanel.RowOf(page, "Quatermass");
            await Expect(row).ToBeVisibleAsync(new() { Timeout = PassMs });
            var listed = (await rig.ListedFileAsync(film))!;

            await row.GetByRole(AriaRole.Button, new() { Name = "Remove…", Exact = true }).ClickAsync();

            var dialog = page.GetByTestId("activity-remove-dialog");
            await Expect(dialog).ToBeVisibleAsync(new() { Timeout = PushMs });
            var mark = log.Mark();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await row.GetByRole(AriaRole.Button, new() { Name = "Remove…", Exact = true }).ClickAsync();
            await Expect(dialog).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToBeHiddenAsync();
            Assert.Empty(log.Since(mark));
            Assert.NotNull(await rig.ListedFileAsync(film));
            await Expect(row).ToBeVisibleAsync();

            await row.GetByRole(AriaRole.Button, new() { Name = "Remove…", Exact = true }).ClickAsync();
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByTestId("activity-remove-dialog-choice-keep").GetByRole(AriaRole.Radio).CheckAsync();
            mark = log.Mark();
            var removed = await page.RunAndWaitForResponseAsync(
                () => dialog.GetByTestId("activity-remove-dialog-confirm").ClickAsync(),
                response => response.Request.Method == "DELETE");

            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"{FilesRoute}/{(long)listed["id"]!}", sent.PathAndQuery);
            Assert.Equal("keep", (string?)JsonNode.Parse(sent.Body!)!["resolution"]);
            var detail = (string?)JsonNode.Parse(await removed.TextAsync())!["detail"] ?? "Done.";
            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync(detail, new() { Timeout = PushMs });
            await Expect(row).ToHaveCountAsync(0);
            Assert.Null(await rig.ListedFileAsync(film));
            Assert.True(rig.WatchedFileExists("r-quatermass", film), "Keep must leave the file in the watched folder.");
            await AssertNotReloadedAsync(page);
        });
    }

    [E2EFact]
    public async Task Remove_on_a_file_whose_original_is_gone_has_nothing_to_choose_and_drops_it_at_once()
    {
        await using var rig = await ProcessingRig.StartAsync(holdFailures: true);
        await rig.WithEvidenceAsync(async () =>
        {
            const string film = "Brides Of Fu 1966.mkv";
            var (page, log) = await OpenDashboardAsync(rig);
            await rig.DropFileAsync("r-brides", film, failFirstPass: true);
            var row = NeedsYouPanel.RowOf(page, "Brides Of Fu");
            await Expect(row).ToBeVisibleAsync(new() { Timeout = PassMs });
            var listed = (await rig.ListedFileAsync(film))!;
            rig.DeleteWatchedRelease("r-brides");
            var mark = log.Mark();

            await row.GetByRole(AriaRole.Button, new() { Name = "Remove…", Exact = true }).ClickAsync();

            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync("Removed from the list. The file on disk is untouched.", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("activity-remove-dialog")).ToHaveCountAsync(0);
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("DELETE", sent.Method);
            Assert.Equal($"{FilesRoute}/{(long)listed["id"]!}", sent.PathAndQuery);
            await Expect(row).ToHaveCountAsync(0);
            Assert.Null(await rig.ListedFileAsync(film));
        });
    }
}
