using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The controls that narrow Activity, in a real browser against a Weir that holds a file in every state: the workflow and how-far-back pickers
/// and the search each write the choice in the address and ask the server for exactly that, and the Columns menu moves a column, keeps the
/// order for the next visit and puts it back, for the list of files and for the list of kept files.
/// </summary>
public sealed partial class ActivityFilterControlsTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task The_workflow_picker_lists_the_files_of_one_workflow_and_asks_the_server_for_that_workflow_and_All_workflows_asks_for_them_all()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var moviesId = await rig.MoviesWorkflowIdAsync();
            var tvId = await WorkflowIdAsync(rig, "tv");
            var (page, log) = await OpenActivityAsync(rig);
            var picker = page.GetByRole(AriaRole.Button, new() { Name = "Workflow", Exact = true });
            await Expect(picker).ToContainTextAsync("All workflows");
            var mark = log.ReadMark();

            await picker.ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "TV", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={tvId}(&|$)"), new() { Timeout = PushMs });
            await Expect(picker).ToContainTextAsync("TV");
            await Expect(Rows(page)).ToHaveCountAsync(0, new() { Timeout = PushMs });
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Files" })).ToContainTextAsync("Nothing yet.");
            await ExpectAskedAsync(log, mark, "/api/v1/processing/files?", $"library_id={tvId}");
            await ExpectAskedAsync(log, mark, "/api/v1/processing/library-cleans?", $"library_id={tvId}");

            mark = log.ReadMark();
            await picker.ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "Movies", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={moviesId}(&|$)"), new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            await ExpectAskedAsync(log, mark, "/api/v1/processing/files?", $"library_id={moviesId}");

            mark = log.ReadMark();
            await picker.ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "All workflows", Exact = true }).ClickAsync();

            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]library="), new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            Assert.DoesNotContain(log.ReadsSince(mark).Where(IsTheListsOwnRead), read => read.PathAndQuery.Contains("library_id=", StringComparison.Ordinal));
        });
    }

    [E2EFact]
    public async Task The_how_far_back_picker_asks_the_server_for_that_many_days_and_Everything_kept_asks_for_no_limit()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenActivityAsync(rig);
            var picker = page.GetByRole(AriaRole.Button, new() { Name = "How far back", Exact = true });

            foreach (var (label, within, days) in new[] { ("Today", "1", "1"), ("Last 30 days", "30", "30") })
            {
                var mark = log.ReadMark();
                await picker.ClickAsync();
                await page.GetByRole(AriaRole.Option, new() { Name = label, Exact = true }).ClickAsync();

                await Expect(picker).ToContainTextAsync(label);
                await Expect(page).ToHaveURLAsync(new Regex($"[?&]within={within}(&|$)"), new() { Timeout = PushMs });
                await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
                await ExpectAskedAsync(log, mark, "/api/v1/processing/files?", $"within_days={days}");
                await ExpectAskedAsync(log, mark, "/api/v1/processing/library-cleans?", $"within_days={days}");
            }

            var before = log.ReadMark();
            await picker.ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "Everything kept", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]within=all(&|$)"), new() { Timeout = PushMs });
            await Expect(picker).ToContainTextAsync("Everything kept");
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            await ExpectAskedAsync(log, before, "/api/v1/processing/files?", "sort=when");
            Assert.DoesNotContain(log.ReadsSince(before).Where(IsTheListsOwnRead), read => read.PathAndQuery.Contains("within_days=", StringComparison.Ordinal));
        });
    }

    [E2EFact]
    public async Task Searching_a_file_by_Enter_or_by_leaving_the_box_asks_the_server_for_it_and_clearing_the_search_lists_everything_again()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenActivityAsync(rig);
            var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a file" });
            var mark = log.ReadMark();

            await search.FillAsync("finished film");
            await search.PressAsync("Enter");

            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]q=finished(%20|\+)film(&|$)"), new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(1, new() { Timeout = PushMs });
            await Expect(Rows(page)).ToContainTextAsync(MixedActivity.Finished);
            await ExpectAskedAsync(log, mark, "/api/v1/processing/files?", "path_contains=finished");
            await ExpectAskedAsync(log, mark, "/api/v1/processing/library-cleans?", "path_contains=finished");

            await search.FillAsync(string.Empty);
            await search.PressAsync("Enter");

            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]q="));

            mark = log.ReadMark();
            await search.FillAsync("held film");
            await page.GetByRole(AriaRole.Region, new() { Name = "Files" }).ClickAsync(new() { Position = new() { X = 8, Y = 8 } });

            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]q=held(%20|\+)film(&|$)"), new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(1, new() { Timeout = PushMs });
            await Expect(Rows(page)).ToContainTextAsync(MixedActivity.Held);
            await ExpectAskedAsync(log, mark, "/api/v1/processing/files?", "path_contains=held");
        });
    }

    [E2EFact]
    public async Task The_Columns_menu_moves_a_column_with_its_cells_keeps_the_order_for_the_next_visit_and_Reset_columns_puts_it_back()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, _) = await OpenActivityAsync(rig);
            Assert.Equal(["file", "status", "when"], await HeadingsAsync(page));
            Assert.Equal(["file", "status", "when"], await CellOrderAsync(page));

            await Heading(page, "When").GetByRole(AriaRole.Button).FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowLeft");

            Assert.Equal(["file", "when", "status"], await HeadingsAsync(page));
            Assert.Equal(["file", "when", "status"], await CellOrderAsync(page));

            await page.ReloadAsync();
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });

            Assert.Equal(["file", "when", "status"], await HeadingsAsync(page));

            await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns" }).ClickAsync();

            Assert.Equal(["file", "status", "when"], await HeadingsAsync(page));
            Assert.Equal(["file", "status", "when"], await CellOrderAsync(page));
        });
    }

    [E2EFact]
    public async Task The_Kept_list_has_a_Columns_menu_of_its_own_that_moves_and_resets_its_columns()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, _) = await OpenActivityAsync(rig);
            await Rows(page).Filter(new() { HasText = MixedActivity.Failed }).GetByRole(AriaRole.Button).ClickAsync();
            await page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button, new() { Name = "Remove from list", Exact = true }).ClickAsync();
            var dialog = page.GetByTestId("activity-remove-dialog");
            await dialog.GetByTestId("activity-remove-dialog-choice-keep").GetByRole(AriaRole.Radio).CheckAsync();
            await dialog.GetByTestId("activity-remove-dialog-confirm").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync(new() { Timeout = PushMs });
            await Chip(page, "Kept").ClickAsync();
            var table = page.GetByTestId("kept-files-table");
            await Expect(table).ToContainTextAsync(MixedActivity.Failed, new() { Timeout = PushMs });
            Assert.Equal(["file", "workflow", "size", "actions"], await HeadingsAsync(page));

            await Heading(page, "Size").GetByRole(AriaRole.Button).FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowLeft");

            Assert.Equal(["file", "size", "workflow", "actions"], await HeadingsAsync(page));
            Assert.Equal(["file", "size", "workflow", "actions"], await table.Locator("tbody tr").First.Locator("td[data-col]").EvaluateAllAsync<string[]>("cells => cells.map(cell => cell.dataset.col)"));

            await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns" }).ClickAsync();

            Assert.Equal(["file", "workflow", "size", "actions"], await HeadingsAsync(page));
        });
    }

    private static ILocator Chip(IPage page, string name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new($"^{Regex.Escape(name)}") });

    private static ILocator Heading(IPage page, string name) => page.GetByRole(AriaRole.Columnheader, new() { Name = name, Exact = true });

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });

    private static async Task<IReadOnlyList<string>> HeadingsAsync(IPage page) =>
        await page.GetByRole(AriaRole.Columnheader).EvaluateAllAsync<string[]>("heads => heads.map(head => head.dataset.col)");

    private static async Task<IReadOnlyList<string>> CellOrderAsync(IPage page) =>
        await Rows(page).First.Locator("td[data-col]").EvaluateAllAsync<string[]>("cells => cells.map(cell => cell.dataset.col)");

    /// <summary>The list's own reads, which ask for a sort; the menu's badge reads files too, by status and with no period.</summary>
    private static bool IsTheListsOwnRead(SentRequest read) =>
        read.PathAndQuery.StartsWith("/api/v1/processing/library-cleans?", StringComparison.Ordinal)
        || (read.PathAndQuery.StartsWith("/api/v1/processing/files?", StringComparison.Ordinal) && read.PathAndQuery.Contains("sort=", StringComparison.Ordinal));

    /// <summary>Waits until the page has asked the server for this route with this parameter since <paramref name="mark"/>.</summary>
    private static async Task ExpectAskedAsync(RequestLog log, int mark, string route, string parameter) =>
        await Poll.UntilAsync(
            () => Task.FromResult(log.ReadsSince(mark).Any(read => read.PathAndQuery.StartsWith(route, StringComparison.Ordinal) && read.PathAndQuery.Contains(parameter, StringComparison.Ordinal))),
            $"the page to ask {route} for {parameter}",
            TimeSpan.FromMilliseconds(PushMs));

    private static async Task<long> WorkflowIdAsync(ProcessingRig rig, string mediaType)
    {
        var listed = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries");
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return (long)listed.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == mediaType)["id"]!;
    }

    private async Task<(IPage Page, RequestLog Log)> OpenActivityAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Wide);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
        return (page, log);
    }
}
