using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The buttons for one file in Activity, in a real browser against a Weir that holds a file in each state, with the server's database held
/// where a test needs the server slow to answer or unable to: Try again says it is queueing and turns the row's buttons off, then shows what the
/// server answered; Pass through unchanged asks first and only a yes queues it; Remove from list forgets the title and leaves the file on
/// disk. Each failure says so in plain words and leaves the file as it was.
/// </summary>
public sealed partial class ActivityFileActionsTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private const float PassMs = 180_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task Try_again_says_Queueing_and_turns_the_buttons_off_while_the_server_takes_it_then_shows_the_servers_answer_and_the_file_finishes()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: false);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.Failed);
            var mark = log.Mark();
            var answered = page.WaitForResponseAsync(response => response.Url.EndsWith("/requeue", StringComparison.Ordinal));

            using (rig.HoldWrites())
            {
                await actions.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();

                var queueing = actions.GetByRole(AriaRole.Button, new() { Name = "Queueing…", Exact = true });
                await Expect(queueing).ToBeDisabledAsync();
                foreach (var button in await actions.GetByRole(AriaRole.Button).AllAsync())
                {
                    await Expect(button).ToBeDisabledAsync();
                }

                await Expect(actions.GetByRole(AriaRole.Status)).ToHaveCountAsync(0);
            }

            var response = await answered;
            Assert.Equal(200, response.Status);
            var detail = (string)JsonNode.Parse(await response.TextAsync())!["detail"]!;
            await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync(detail, new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.Matches(@"^/api/v1/processing/files/\d+/requeue$", sent.PathAndQuery);
            await Expect(actions.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true })).ToHaveCountAsync(0, new() { Timeout = PushMs });

            rig.ReleasePass(MixedActivity.Working);

            await Expect(Row(page, MixedActivity.Failed)).ToContainTextAsync("Done", new() { Timeout = PassMs });
            Assert.Equal("processed", (string)(await rig.ListedFileAsync(MixedActivity.Failed))!["status"]!);
        });
    }

    [E2EFact]
    public async Task When_the_server_cannot_queue_it_Try_again_says_so_and_the_file_stays_failed_and_can_be_tried_again()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: true);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, _) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.Failed);
            var tryAgain = actions.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true });

            using (rig.HoldWrites())
            {
                var refused = await page.RunAndWaitForResponseAsync(() => tryAgain.ClickAsync(), response => response.Url.EndsWith("/requeue", StringComparison.Ordinal), new() { Timeout = PushMs });

                Assert.Equal(503, refused.Status);
                await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync("Could not queue that file again.", new() { Timeout = PushMs });
            }

            await Expect(tryAgain).ToBeEnabledAsync();
            await Expect(Row(page, MixedActivity.Failed)).ToContainTextAsync("Failed");
            Assert.Equal("processing_failed", (string)(await rig.ListedFileAsync(MixedActivity.Failed))!["status"]!);

            await tryAgain.ClickAsync();

            await Expect(actions.GetByRole(AriaRole.Status)).Not.ToHaveTextAsync("Could not queue that file again.", new() { Timeout = PushMs });
            await Expect(tryAgain).ToHaveCountAsync(0, new() { Timeout = PushMs });
        });
    }

    [E2EFact]
    public async Task When_the_server_cannot_queue_it_Process_again_says_so_and_the_finished_file_is_left_as_it_was()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: true);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.Finished);
            var processAgain = actions.GetByRole(AriaRole.Button, new() { Name = "Process again", Exact = true });
            var mark = log.Mark();

            using (rig.HoldWrites())
            {
                var refused = await page.RunAndWaitForResponseAsync(() => processAgain.ClickAsync(), response => response.Url.EndsWith("/requeue", StringComparison.Ordinal), new() { Timeout = PushMs });

                Assert.Equal(503, refused.Status);
                await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync("Could not queue that file again.", new() { Timeout = PushMs });
            }

            await Expect(processAgain).ToBeEnabledAsync();
            Assert.Equal("processed", (string)(await rig.ListedFileAsync(MixedActivity.Finished))!["status"]!);
            Assert.All(log.Since(mark), sent => Assert.Matches(@"^/api/v1/processing/files/\d+/requeue$", sent.PathAndQuery));
        });
    }

    [E2EFact]
    public async Task Pass_through_unchanged_asks_first_Not_now_and_Escape_change_nothing_a_refusal_is_told_and_Yes_queues_it_with_the_choice_in_the_request()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: true);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.Failed);
            var dialog = page.GetByTestId("activity-pass-through-confirm");
            var open = actions.GetByRole(AriaRole.Button, new() { Name = "Pass through unchanged", Exact = true });
            var mark = log.Mark();

            await open.ClickAsync();
            await Expect(dialog).ToContainTextAsync("Pass this file through unchanged?");
            await dialog.GetByTestId("activity-pass-through-confirm-cancel").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);

            await open.ClickAsync();
            await Expect(dialog).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);

            log.AssertNothingSentSince(mark);
            Assert.Equal("processing_failed", (string)(await rig.ListedFileAsync(MixedActivity.Failed))!["status"]!);

            await open.ClickAsync();
            using (rig.HoldWrites())
            {
                var refused = await page.RunAndWaitForResponseAsync(
                    () => dialog.GetByTestId("activity-pass-through-confirm-confirm").ClickAsync(),
                    response => response.Url.EndsWith("/enqueue", StringComparison.Ordinal),
                    new() { Timeout = PushMs });

                Assert.Equal(503, refused.Status);
                await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync(
                    "Could not queue that file.",
                    new() { Timeout = PushMs });
            }

            await open.ClickAsync();
            mark = log.Mark();
            await dialog.GetByTestId("activity-pass-through-confirm-confirm").ClickAsync();

            await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync(
                "Queued to pass through unchanged. Weir checks the copy before removing the original.",
                new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.EndsWith("/processing/jobs/file-remux-pass/enqueue", sent.PathAndQuery, StringComparison.Ordinal);
            var body = JsonNode.Parse(sent.Body!)!;
            Assert.True((bool)body["pass_through_unchanged"]!);
            Assert.Equal($"h-failed/{MixedActivity.Failed}", (string)body["relative_media_path"]!);
            Assert.Equal("movie", (string)body["media_scope"]!);
        });
    }

    [E2EFact]
    public async Task Remove_from_list_on_a_title_with_no_choice_forgets_it_at_once_leaves_the_file_on_disk_and_moves_the_selection_on_and_a_refusal_is_told()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: true);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.ExtraOfFinished);
            var remove = actions.GetByRole(AriaRole.Button, new() { Name = "Remove from list", Exact = true });
            var listed = (long)(await rig.ListedFileAsync(MixedActivity.ExtraOfFinished))!["id"]!;

            using (rig.HoldWrites())
            {
                var refused = await page.RunAndWaitForResponseAsync(() => remove.ClickAsync(), response => response.Request.Method == "DELETE", new() { Timeout = PushMs });

                Assert.Equal(503, refused.Status);
                await Expect(actions.GetByRole(AriaRole.Status)).ToHaveTextAsync("Could not remove that file from the list.", new() { Timeout = PushMs });
            }

            Assert.NotNull(await rig.ListedFileAsync(MixedActivity.ExtraOfFinished));
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count);
            var mark = log.Mark();

            await remove.ClickAsync();

            await Expect(Row(page, MixedActivity.ExtraOfFinished)).ToHaveCountAsync(0, new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count - 1);
            await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Removed from the list. The file on disk is untouched." }).First).ToBeVisibleAsync();
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("DELETE", sent.Method);
            Assert.Equal($"/api/v1/processing/files/{listed}", sent.PathAndQuery);
            Assert.Null(await rig.ListedFileAsync(MixedActivity.ExtraOfFinished));
            Assert.True(rig.WatchedFileExists("h-finished", MixedActivity.ExtraOfFinished), "Remove from list must not touch the file.");
            await Expect(page.GetByTestId("activity-detail")).Not.ToContainTextAsync(MixedActivity.ExtraOfFinished);
            await Expect(Rows(page).First).ToHaveAttributeAsync("aria-current", "true");
        });
    }

    [E2EFact]
    public async Task While_the_server_takes_the_removal_the_dialog_says_Removing_and_Escape_and_a_click_beside_it_do_nothing_until_the_server_is_done()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: false);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenAsync(rig);
            var actions = await SelectAsync(page, MixedActivity.Failed);
            await actions.GetByRole(AriaRole.Button, new() { Name = "Remove from list", Exact = true }).ClickAsync();
            var dialog = page.GetByTestId("activity-remove-dialog");
            await dialog.GetByTestId("activity-remove-dialog-choice-keep").GetByRole(AriaRole.Radio).CheckAsync();
            var confirm = dialog.GetByTestId("activity-remove-dialog-confirm");
            var mark = log.Mark();

            using (rig.HoldWrites())
            {
                await confirm.ClickAsync();

                await Expect(confirm).ToHaveTextAsync("Removing…");
                await Expect(confirm).ToBeDisabledAsync();
                await page.Keyboard.PressAsync("Escape");
                // The layer is marked busy, so the click is made whether or not the browser thinks it can be clicked.
                var layer = (await dialog.BoundingBoxAsync())!;
                await dialog.ClickAsync(new() { Position = new() { X = layer.Width - 10, Y = 10 }, Force = true });
                await Expect(dialog).ToBeVisibleAsync();
                Assert.NotNull(await rig.ListedFileAsync(MixedActivity.Failed));
            }

            await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
            Assert.Null(await rig.ListedFileAsync(MixedActivity.Failed));
            Assert.Equal("DELETE", Assert.Single(log.Since(mark)).Method);
        });
    }

    private async Task<(IPage Page, RequestLog Log)> OpenAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Wide);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
        return (page, log);
    }

    private static async Task<ILocator> SelectAsync(IPage page, string file)
    {
        await Row(page, file).GetByRole(AriaRole.Button).ClickAsync();
        await Expect(Row(page, file)).ToHaveAttributeAsync("aria-current", "true");
        var actions = page.GetByTestId("activity-file-actions");
        await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(file);
        return actions;
    }

    private static ILocator Row(IPage page, string file) => Rows(page).Filter(new() { HasText = file });

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });
}
