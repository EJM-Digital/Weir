using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Dashboard's Needs you panel for files that wait on a decision: a skipped file asks its workflow to look again, and a rejected one is
/// processed again alone or, from the group, together with the others after the rules or the file changed.
/// </summary>
public sealed class NeedsYouSkippedAndRejectedTests(E2EServer server) : E2ETestBase(server)
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
        return (page, log);
    }

    [E2EFact]
    public async Task Check_again_on_a_skipped_file_asks_its_workflow_to_look_again_and_the_file_is_cleaned_once_the_rule_has_changed()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            const string film = "Skipme 1999.mkv";
            await rig.SetExcludePatternsAsync("*skipme*");
            var (page, log) = await OpenDashboardAsync(rig);
            await rig.PlaceFileAsync("r-skip", film);
            await rig.ScanAsync();
            var row = NeedsYouPanel.RowOf(page, "Skipme");
            await Expect(row).ToBeVisibleAsync(new() { Timeout = PassMs });
            await Expect(row).ToContainTextAsync("Excluded by a rule");
            Assert.Equal("skipped", (string)(await rig.ListedFileAsync(film))!["status"]!);
            await rig.SetExcludePatternsAsync(string.Empty);
            var workflow = await rig.MoviesWorkflowIdAsync();
            var mark = log.Mark();

            await row.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true }).ClickAsync();

            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync("Checking this workflow again.", new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.Equal("movie", (string?)body["media_scope"]);
            Assert.Equal(workflow, (long)body["library_id"]!);
            Assert.True((bool)body["enqueue_remux_jobs"]!);
            await rig.WaitForStatusAsync(film, "processed");
            await Expect(row).ToHaveCountAsync(0, new() { Timeout = PushMs });
        });
    }

    [E2EFact]
    public async Task Check_again_is_unavailable_for_a_workflow_a_media_manager_hands_its_downloads_to_and_says_why()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            const string film = "Skipme 1999.mkv";
            await rig.SetExcludePatternsAsync("*skipme*");
            var (page, log) = await OpenDashboardAsync(rig);
            await rig.PlaceFileAsync("r-skip", film);
            await rig.ScanAsync();
            var row = NeedsYouPanel.RowOf(page, "Skipme");
            await Expect(row).ToBeVisibleAsync(new() { Timeout = PassMs });
            await rig.LinkMoviesToDelunoAsync();
            var check = row.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true });

            await Expect(check).ToBeDisabledAsync();
            await Expect(check).ToHaveAttributeAsync("title", new Regex("hand"));
            var mark = log.Mark();
            await check.ClickAsync(new() { Force = true });
            log.AssertNothingSentSince(mark);
        });
    }

    [E2EFact]
    public async Task Process_again_on_a_rejected_file_queues_it_alone_and_Process_all_again_asks_first_then_queues_the_rest()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            const string first = "Grey Gardens 1975.mkv";
            const string second = "Salesman 1969.mkv";
            var (page, log) = await OpenDashboardAsync(rig);
            await rig.DropRejectedFileAsync("r-grey", first);
            await rig.DropRejectedFileAsync("r-salesman", second);
            var firstRow = NeedsYouPanel.RowOf(page, "Grey Gardens");
            var secondRow = NeedsYouPanel.RowOf(page, "Salesman");
            await Expect(firstRow).ToBeVisibleAsync(new() { Timeout = PassMs });
            await Expect(secondRow).ToBeVisibleAsync(new() { Timeout = PassMs });
            Assert.Equal("rejected", (string)(await rig.ListedFileAsync(first))!["status"]!);
            var listed = (await rig.ListedFileAsync(first))!;

            // The first film now has the audio it lacked; "Process again" queues only that file.
            await rig.MakeAcceptableAsync("r-grey", first);
            var mark = log.Mark();
            await firstRow.GetByRole(AriaRole.Button, new() { Name = "Process again", Exact = true }).ClickAsync();

            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync("Queued again.", new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.Equal($"{FilesRoute}/{(long)listed["id"]!}/requeue", sent.PathAndQuery);
            await rig.WaitForStatusAsync(first, "processing");
            Assert.Equal("rejected", (string)(await rig.ListedFileAsync(second))!["status"]!);

            // The second film is fixed too; the group's button counts first and asks, and "Not now" queues nothing.
            await rig.MakeAcceptableAsync("r-salesman", second);
            await Expect(secondRow).ToBeVisibleAsync();
            var group = page.GetByTestId("live-needs");
            var all = group.GetByRole(AriaRole.Button, new() { Name = "Process all again", Exact = true });
            await all.ClickAsync();
            var dialog = page.GetByTestId("activity-process-rejected-confirm");
            await Expect(dialog).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(dialog).ToContainTextAsync("Process 1 rejected file again with your current rules?");
            mark = log.Mark();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Not now", Exact = true }).ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            log.AssertNothingSentSince(mark);
            Assert.Equal("rejected", (string)(await rig.ListedFileAsync(second))!["status"]!);

            await all.ClickAsync();
            await Expect(dialog).ToBeVisibleAsync(new() { Timeout = PushMs });
            mark = log.Mark();
            await dialog.GetByTestId("activity-process-rejected-confirm-confirm").ClickAsync();

            await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
            await Expect(NeedsYouPanel.Notice(page)).ToHaveTextAsync("Queued 1 file.", new() { Timeout = PushMs });
            Assert.Single(log.Since(mark));
            await Expect(secondRow).ToHaveCountAsync(0, new() { Timeout = PushMs });
            await Expect(firstRow).ToHaveCountAsync(0);
        });
    }
}
