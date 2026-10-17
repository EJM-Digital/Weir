using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The workflow picker on the Library page in a real browser, against a Weir with seven workflows: it opens on the search box, moves
/// and picks from the keyboard, closes on Escape and on a click beside it, finds a workflow by name, and choosing one shows that
/// workflow's files, writes it in the address, remembers it for the next visit and drops what was selected in the one before.
/// </summary>
public sealed class LibraryPickerTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";
    private const string Akira = "Akira (1988)/Akira (1988).mkv";

    [E2EFact]
    public async Task The_list_opens_on_its_search_box_moves_with_the_arrow_keys_Home_and_End_and_Escape_closes_it_with_focus_back_on_the_picker()
    {
        await using var rig = await LibraryRig.StartAsync();
        await AddWorkflowsAsync(rig, "Anime", "Kids", "Documentaries", "Classics");
        var page = await OpenLibraryAsync(rig, expectedRows: 0);
        var trigger = Trigger(page);
        var list = List(page);
        var options = list.GetByRole(AriaRole.Option);
        var search = list.GetByRole(AriaRole.Searchbox, new() { Name = "Find a workflow" });

        await trigger.ClickAsync();

        await Expect(list).ToBeVisibleAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(search).ToBeFocusedAsync();
        var count = await options.CountAsync();
        Assert.True(count >= 7, $"The list holds {count} workflows.");

        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(options.First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(options.Nth(1)).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("End");
        await Expect(options.Last).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(options.First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowUp");
        await Expect(options.Last).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Home");
        await Expect(options.First).ToBeFocusedAsync();

        await search.FocusAsync();
        await page.Keyboard.PressAsync("ArrowUp");
        await Expect(options.Last).ToBeFocusedAsync();
        await search.FocusAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(options.First).ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Escape");

        await Expect(list).ToHaveCountAsync(0);
        await Expect(trigger).ToBeFocusedAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
    }

    [E2EFact]
    public async Task Typing_finds_a_workflow_by_name_and_says_when_there_is_none_and_a_click_beside_the_list_closes_it()
    {
        await using var rig = await LibraryRig.StartAsync();
        await AddWorkflowsAsync(rig, "Anime", "Kids", "Documentaries", "Classics");
        var page = await OpenLibraryAsync(rig, expectedRows: 0);
        var list = List(page);
        var search = list.GetByRole(AriaRole.Searchbox, new() { Name = "Find a workflow" });
        await Trigger(page).ClickAsync();

        await search.FillAsync("docu");

        await Expect(list.GetByRole(AriaRole.Option)).ToHaveCountAsync(1);
        await Expect(list.GetByRole(AriaRole.Option)).ToContainTextAsync("Documentaries");

        await search.FillAsync("zzz");

        await Expect(list.GetByRole(AriaRole.Option)).ToHaveCountAsync(0);
        await Expect(list).ToContainTextAsync("No workflow by that name.");

        await page.GetByTestId("library-page").ClickAsync(new() { Position = new() { X = 4, Y = 4 } });

        await Expect(list).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Enter_and_Space_pick_the_focused_workflow_and_focus_goes_back_to_the_picker()
    {
        await using var rig = await LibraryRig.StartAsync();
        var anime = await rig.AddWorkflowAsync("Anime");
        var kids = await rig.AddWorkflowAsync("Kids");
        var page = await OpenLibraryAsync(rig, expectedRows: 0);
        var trigger = Trigger(page);

        await trigger.ClickAsync();
        await List(page).GetByRole(AriaRole.Option, new() { NameRegex = new("^Anime") }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        await Expect(trigger).ToContainTextAsync("Anime");
        await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={anime.Id}"), new() { Timeout = ServerMs });
        await Expect(trigger).ToBeFocusedAsync();
        await Expect(List(page)).ToHaveCountAsync(0);

        await trigger.ClickAsync();
        await List(page).GetByRole(AriaRole.Option, new() { NameRegex = new("^Kids") }).FocusAsync();
        await page.Keyboard.PressAsync("Space");

        await Expect(trigger).ToContainTextAsync("Kids");
        await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={kids.Id}"), new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task Choosing_another_workflow_lists_its_files_writes_it_in_the_address_remembers_it_and_drops_the_selection()
    {
        await using var rig = await LibraryRig.StartAsync();
        await rig.AddFilmAsync(Arrival, ["eng"], []);
        await rig.AddFilmAsync(Heat, ["eng"], []);
        var anime = await rig.AddWorkflowAsync("Anime");
        await LibraryRig.AddFilmToAsync(anime, Akira, "jpn");
        await rig.ScanAsync();
        await rig.ScanAsync(anime.Id);
        var page = await OpenLibraryAsync(rig, expectedRows: 2);
        var log = RequestLog.Watch(page);
        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Select ") }).First.CheckAsync();
        await Expect(page.GetByTestId("library-bulk")).ToContainTextAsync("1 selected");
        await Trigger(page).ClickAsync();
        await Expect(List(page).GetByRole(AriaRole.Option, new() { NameRegex = new("^E2E library films") })).ToContainTextAsync("2 files");
        var mark = log.ReadMark();

        await List(page).GetByRole(AriaRole.Option, new() { NameRegex = new("^Anime") }).ClickAsync();

        await Expect(Trigger(page)).ToContainTextAsync("Anime");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToContainTextAsync("Akira (1988).mkv");
        await Expect(page.GetByTestId("library-bulk")).ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={anime.Id}"));
        Assert.Contains(log.ReadsSince(mark), read => read.PathAndQuery.StartsWith($"/api/v1/processing/libraries/{anime.Id}/library-files", StringComparison.Ordinal));

        await page.GotoAsync($"{rig.BaseUrl}/library");

        await Expect(Trigger(page)).ToContainTextAsync("Anime", new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToContainTextAsync("Akira (1988).mkv", new() { Timeout = ServerMs });
    }

    private static ILocator Trigger(IPage page) => page.GetByTestId("library-picker").GetByRole(AriaRole.Button).First;

    private static ILocator List(IPage page) => page.GetByRole(AriaRole.Listbox, new() { Name = "Choose a workflow" });

    private static async Task AddWorkflowsAsync(LibraryRig rig, params string[] names)
    {
        foreach (var name in names)
        {
            await rig.AddWorkflowAsync(name);
        }
    }

    private async Task<IPage> OpenLibraryAsync(LibraryRig rig, int expectedRows)
    {
        var page = await NewPageAsync(Desktop);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(Trigger(page)).ToBeVisibleAsync(new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(expectedRows, new() { Timeout = ServerMs });
        return page;
    }
}
