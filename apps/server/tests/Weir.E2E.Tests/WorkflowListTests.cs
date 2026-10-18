using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Workflows › File paths, the list: what each row says, and what a person can do to the list itself.</summary>
public sealed class WorkflowListTests(E2EServer server) : E2ETestBase(server)
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string ProfileName = "E2E list profile";

    [E2EFact]
    public async Task Each_row_shows_its_place_name_type_folders_rules_and_the_work_in_progress()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        var library = Directory.CreateDirectory(Path.Join(folders.Path, "library")).FullName;
        var profileId = await WeirProfiles.CreateAsync(admin, ProfileName);
        var cinema = await CreateAsync(admin, "E2E Cinema", "movie", watched, output);
        var anime = await CreateAsync(admin, "E2E Anime", "tv");
        var half = await CreateAsync(admin, "E2E Half set", "movie");
        try
        {
            await WeirProfiles.UseInWorkflowAsync(admin, cinema, profileId);
            await WeirWorkflowApi.SetLibraryFoldersAsync(page.Context, BaseUrl, cinema, library);
            await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);
            await WeirWorkflowApi.StartLibraryScanAsync(page.Context, BaseUrl, cinema);
            await WorkflowJobs.WaitUntilBusyAsync(admin, cinema);
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);

            var inOrder = (await admin.GetAsync(Libraries)).Elements.Select(row => row!.AsObject()).OrderBy(row => (int)row["display_order"]!).ToList();
            foreach (var id in new[] { cinema, anime, half })
            {
                var row = WorkflowEditor.Row(page, id);
                var expectedPlace = inOrder.FindIndex(workflow => (long)workflow["id"]! == id) + 1;
                await Expect(row.Locator(".mm-priority__number")).ToHaveTextAsync(expectedPlace.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var cinemaRow = WorkflowEditor.Row(page, cinema);
            await Expect(cinemaRow.Locator("[data-col=workflow]")).ToContainTextAsync("E2E Cinema");
            await Expect(cinemaRow.Locator("[data-col=workflow]")).ToContainTextAsync("Movies");
            await Expect(cinemaRow.Locator("[data-col=workflow]")).ToContainTextAsync(new Regex(@"\d+ in progress"));
            await Expect(cinemaRow.Locator("[data-col=watches]")).ToContainTextAsync(watched);
            await Expect(cinemaRow.Locator("[data-col=cleans]")).ToContainTextAsync(output);
            await Expect(cinemaRow.Locator("[data-col=rules]")).ToHaveTextAsync(ProfileName);

            var animeRow = WorkflowEditor.Row(page, anime);
            await Expect(animeRow.Locator("[data-col=workflow]")).ToContainTextAsync("TV episodes");
            await Expect(animeRow.Locator("[data-col=workflow]")).Not.ToContainTextAsync("in progress");
            await Expect(animeRow.Locator("[data-col=watches]")).ToHaveTextAsync("Not watching a folder");
            await Expect(animeRow.Locator("[data-col=cleans]")).ToHaveTextAsync("Cleans in place");
            await Expect(animeRow.Locator("[data-col=rules]")).ToHaveTextAsync(await DefaultProfileNameAsync(admin, anime));

            // Weir will not keep a watched folder with nowhere to write, and says why in the editor.
            await WorkflowEditor.Row(page, half).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await WorkflowEditor.Text(WorkflowEditor.Form(page), "Watched folder").FillAsync(watched);
            await WorkflowEditor.Save(page).ClickAsync();
            await Expect(WorkflowEditor.Form(page).GetByTestId("processing-library-save-error")).ToContainTextAsync("Set an output folder as well as a watched folder, so processed files have somewhere to go.");
            Assert.Equal(string.Empty, (string)(await WorkflowEditor.ReadAsync(admin, half))["watched_folder"]!);
        }
        finally
        {
            await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);
            foreach (var id in new[] { cinema, anime, half })
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            }

            await WeirProfiles.ClearAsync(admin, profileId);
        }
    }

    [E2EFact]
    public async Task Dragging_a_grip_moves_the_workflow_in_the_order_and_saves_it_and_Escape_puts_it_back()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        long[] ids = [await CreateAsync(admin, "E2E Drag first", "movie"), await CreateAsync(admin, "E2E Drag second", "movie"), await CreateAsync(admin, "E2E Drag third", "movie")];
        try
        {
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
            var log = RequestLog.Watch(page);
            var before = await OrderAsync(admin);

            // Pressed and moved, then Escape: the row goes back and nothing is sent.
            var mark = log.Mark();
            var grip = Grip(page, "E2E Drag third");
            await DragAsync(page, grip, WorkflowEditor.Row(page, ids[0]), release: false);
            await Expect(WorkflowEditor.Row(page, ids[2])).ToHaveAttributeAsync("data-grabbed", "true");
            await page.Keyboard.PressAsync("Escape");
            await page.Mouse.UpAsync();
            await Expect(WorkflowEditor.Row(page, ids[2])).Not.ToHaveAttributeAsync("data-grabbed", "true");
            await RowsAtRestAsync(page);
            log.AssertNothingSentSince(mark);
            Assert.Equal(before, await OrderAsync(admin));

            // Dropped above the first of the three, the third takes its place and the server keeps the order.
            await DragAsync(page, grip, WorkflowEditor.Row(page, ids[0]), release: true);
            var expected = before.Where(id => id != ids[2]).ToList();
            expected.Insert(expected.IndexOf(ids[0]), ids[2]);
            await Poll.UntilAsync(async () => (await OrderAsync(admin)).SequenceEqual(expected), "the server to hold the dragged order");
            var sent = Assert.Single(log.Since(mark), request => request.PathAndQuery.EndsWith("/reorder", StringComparison.Ordinal));
            Assert.Equal("POST", sent.Method);
            Assert.Equal(expected.Select(id => (long?)id), JsonNode.Parse(sent.Body!)!["library_ids_in_order"]!.AsArray().Select(id => (long?)id));
            Assert.Equal(await RowIdsAsync(page), expected);
        }
        finally
        {
            foreach (var id in ids)
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            }
        }
    }

    [E2EFact]
    public async Task A_new_order_Weir_refuses_because_a_workflow_was_added_meanwhile_is_explained_and_the_list_returns_to_the_saved_order()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        long[] ids = [await CreateAsync(admin, "E2E Refused first", "movie"), await CreateAsync(admin, "E2E Refused second", "movie")];
        long? added = null;
        try
        {
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);

            // The order is drawn while another person adds a workflow: the page's new order cannot list it.
            await DragAsync(page, Grip(page, "E2E Refused second"), WorkflowEditor.Row(page, ids[0]), release: false);
            var listedAgain = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.EndsWith("/processing/libraries", StringComparison.Ordinal));
            added = await CreateAsync(admin, "E2E Refused added", "movie");
            await listedAgain;
            await page.Mouse.UpAsync();

            await Expect(page.GetByTestId("processing-library-notice")).ToHaveTextAsync("Reordering must list every workflow exactly once.");
            await RowsAtRestAsync(page);
            Assert.Equal(await OrderAsync(admin), await RowIdsAsync(page));
        }
        finally
        {
            foreach (var id in ids.Concat(added is null ? [] : [added.Value]))
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            }
        }
    }

    [E2EFact]
    public async Task Alt_with_an_arrow_on_a_grip_moves_the_workflow_and_saves_it_and_the_move_is_announced()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        long[] ids = [await CreateAsync(admin, "E2E Key first", "movie"), await CreateAsync(admin, "E2E Key second", "movie")];
        try
        {
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
            var before = await OrderAsync(admin);
            await Grip(page, "E2E Key second").FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowUp");

            var expected = before.Where(id => id != ids[1]).ToList();
            expected.Insert(expected.IndexOf(ids[0]), ids[1]);
            await Poll.UntilAsync(async () => (await OrderAsync(admin)).SequenceEqual(expected), "the server to hold the moved order");
            await Expect(page.GetByTestId("workflow-priority-announcement")).ToContainTextAsync("E2E Key second moved to position");
            Assert.Equal(await RowIdsAsync(page), expected);
        }
        finally
        {
            foreach (var id in ids)
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            }
        }
    }

    [E2EFact]
    public async Task A_column_moves_with_Alt_and_an_arrow_rows_are_never_sorted_and_Reset_columns_puts_them_back()
    {
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var headings = page.Locator("table.mm-workflow-table thead th");
        var original = await headings.AllInnerTextsAsync();
        await Expect(headings.Last).ToHaveAttributeAsync("data-col", "actions");

        // Headings are plain text: clicking one sorts nothing.
        var order = await RowIdsAsync(page);
        await page.Locator("table.mm-workflow-table thead th[data-col=workflow]").ClickAsync();
        Assert.Equal(order, await RowIdsAsync(page));

        await page.Locator("table.mm-workflow-table thead th[data-col=kind]").FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowLeft");
        await Expect(headings.Nth(2)).ToHaveAttributeAsync("data-col", "workflow");
        await Expect(headings.Nth(1)).ToHaveAttributeAsync("data-col", "kind");
        Assert.Equal(order, await RowIdsAsync(page));

        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns" }).ClickAsync();
        await Expect(headings.Nth(1)).ToHaveAttributeAsync("data-col", "workflow");
        Assert.Equal(original, await headings.AllInnerTextsAsync());
    }

    [E2EFact]
    public async Task A_viewer_sees_the_workflows_with_no_Add_button_no_grips_and_Edit_Remove_and_On_switched_off()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var adminPage = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(adminPage, BaseUrl);
        var id = await CreateAsync(admin, "E2E For a viewer", "movie");
        try
        {
            var page = await NewPageAsync();
            await ViewerAccount.SignInAsync(page, BaseUrl, Server.DatabasePath);
            await Navigation.OpenTabAsync(page, "Workflows", "File paths");
            var row = WorkflowEditor.Row(page, id);
            await Expect(row).ToContainTextAsync("E2E For a viewer");

            await Expect(page.GetByTestId("processing-library-add")).ToHaveCountAsync(0);
            await Expect(page.Locator(".mm-reorder-handle")).ToHaveCountAsync(0);
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true })).ToBeDisabledAsync();
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true })).ToBeDisabledAsync();
            await Expect(row.GetByRole(AriaRole.Radio, new() { Name = "On" })).ToBeDisabledAsync();
            await Expect(row.GetByRole(AriaRole.Radio, new() { Name = "Off" })).ToBeDisabledAsync();
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(adminPage.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task A_switch_Weir_refuses_leaves_the_workflow_as_it_was_and_says_why_until_the_next_change()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var sharedFolder = Directory.CreateDirectory(Path.Join(folders.Path, "shared")).FullName;
        var first = await CreateAsync(admin, "E2E Switch first", "movie", sharedFolder, Directory.CreateDirectory(Path.Join(folders.Path, "first-out")).FullName);
        var second = await CreateAsync(admin, "E2E Switch second", "movie", Directory.CreateDirectory(Path.Join(folders.Path, "second")).FullName, Directory.CreateDirectory(Path.Join(folders.Path, "second-out")).FullName);
        var third = await CreateAsync(admin, "E2E Switch third", "movie");
        try
        {
            // The second workflow ends up watching the first one's folder, as a media manager's sync can leave it.
            E2EDatabase.SetWatchedFolder(Server.DatabasePath, second, sharedFolder);
            await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
            var notice = page.GetByTestId("processing-library-notice");
            var offOfSecond = WorkflowEditor.Row(page, second).GetByRole(AriaRole.Radio, new() { Name = "Off" });
            var offOfThird = WorkflowEditor.Row(page, third).GetByRole(AriaRole.Radio, new() { Name = "Off" });

            await offOfSecond.ClickAsync();
            await Expect(notice).ToContainTextAsync("This workflow's watched folder overlaps the watched folder of 'E2E Switch first'. Each workflow needs its own folders, neither inside another's.");
            await Expect(WorkflowEditor.Row(page, second).GetByRole(AriaRole.Radio, new() { Name = "On" })).ToBeCheckedAsync();
            Assert.True((bool)(await WorkflowEditor.ReadAsync(admin, second))["enabled"]!);

            // The next change clears the notice, and a switch that works saves at once.
            await offOfThird.ClickAsync();
            await Expect(notice).ToHaveCountAsync(0);
            await Expect(offOfThird).ToBeCheckedAsync();
            Assert.False((bool)(await WorkflowEditor.ReadAsync(admin, third))["enabled"]!);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, first);
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, second);
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, third);
        }
    }

    /// <summary>Rows slide to their places when a drag ends; they are measured only once every slide has finished.</summary>
    private static async Task RowsAtRestAsync(IPage page) =>
        await page.WaitForFunctionAsync("() => [...document.querySelectorAll('table.mm-workflow-table tbody tr')].every(row => row.getAnimations().length === 0 && getComputedStyle(row).transform === 'none')");

    private static ILocator Grip(IPage page, string name) => page.GetByRole(AriaRole.Button, new() { Name = $"Move {name}", Exact = true });

    /// <summary>Presses the grip, moves to the top edge of <paramref name="target"/> in steps, and lets go unless told not to.</summary>
    private static async Task DragAsync(IPage page, ILocator grip, ILocator target, bool release)
    {
        var from = (await grip.BoundingBoxAsync())!;
        var to = (await target.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(from.X + (from.Width / 2), from.Y + (from.Height / 2));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(from.X + (from.Width / 2), to.Y + 2, new() { Steps = 12 });
        if (release)
        {
            await page.Mouse.UpAsync();
        }
    }

    private static async Task<List<long>> RowIdsAsync(IPage page)
    {
        var testIds = await page.Locator("table.mm-workflow-table tbody tr").EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.testid)");
        return [.. testIds.Select(id => long.Parse(id["processing-library-".Length..], System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static async Task<List<long>> OrderAsync(WeirClient admin) =>
        [.. (await admin.GetAsync(Libraries)).Elements.Select(row => row!.AsObject()).OrderBy(row => (int)row["display_order"]!).Select(row => (long)row["id"]!)];

    private static async Task<long> CreateAsync(WeirClient admin, string name, string mediaType, string watched = "", string output = "")
    {
        var created = await admin.PostWithCsrfAsync(Libraries, new JsonObject { ["name"] = name, ["media_type"] = mediaType, ["watched_folder"] = watched, ["output_folder"] = output });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    private static async Task<string> DefaultProfileNameAsync(WeirClient admin, long workflowId)
    {
        var profileId = (long)(await WorkflowEditor.ReadAsync(admin, workflowId))["rule_set_id"]!;
        var profiles = (await admin.GetAsync($"{WeirClient.Api}/processing/rule-sets")).Elements.Select(row => row!.AsObject());
        return (string)profiles.Single(row => (long)row["id"]! == profileId)["name"]!;
    }
}
