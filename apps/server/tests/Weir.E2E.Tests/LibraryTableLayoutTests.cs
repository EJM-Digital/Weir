using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// How the Library page lays itself out in a real browser: the last status chips fold into a More menu when the title line is short of
/// room and a folded one can still be chosen, the Files table moves its columns from the keyboard and remembers them, puts them back
/// from the Columns menu, and drops Audio, Subtitles and Back when it is narrow while every cell stays under its own heading.
/// </summary>
public sealed class LibraryTableLayoutTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };
    private static readonly ViewportSize Narrow = new() { Width = 940, Height = 800 };
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 844 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";

    [E2EFact]
    public async Task On_a_phone_the_last_chips_fold_into_More_statuses_and_a_folded_one_can_be_chosen_and_then_stays_on_the_line()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig, Phone);
        var group = page.GetByRole(AriaRole.Group, new() { Name = "Show" });
        var more = group.GetByRole(AriaRole.Button, new() { NameRegex = new("^More") });
        await Expect(more).ToBeVisibleAsync();
        await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Left alone") })).ToHaveCountAsync(0);

        await more.ClickAsync();

        var menu = page.GetByRole(AriaRole.Menu, new() { Name = "More statuses" });
        await Expect(menu).ToBeVisibleAsync();
        var last = menu.GetByRole(AriaRole.Menuitem).Last;
        await Expect(last).ToContainTextAsync("Left alone");
        await last.ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=left_alone"), new() { Timeout = ServerMs });
        var chosen = group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Left alone") });
        await Expect(chosen).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(page.GetByTestId("library-empty")).ToHaveTextAsync("You have not set any file aside.", new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task A_heading_moves_with_Alt_and_an_arrow_its_cells_follow_it_the_order_is_remembered_and_Reset_columns_puts_it_back()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig, Desktop);
        Assert.Equal(["title", "status", "audio", "subtitles", "size", "saved"], await HeadingsAsync(page));
        Assert.Equal(["select", "title", "status", "audio", "subtitles", "size", "saved"], await CellOrderAsync(page));

        await Heading(page, "Subtitles").GetByRole(AriaRole.Button).FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowLeft");

        await Expect(page.GetByRole(AriaRole.Columnheader).Nth(3)).ToContainTextAsync("Subtitles");
        Assert.Equal(["title", "status", "subtitles", "audio", "size", "saved"], await HeadingsAsync(page));
        Assert.Equal(["select", "title", "status", "subtitles", "audio", "size", "saved"], await CellOrderAsync(page));
        await AssertCellsUnderTheirHeadingsAsync(page, "Subtitles", "subtitles");

        await page.ReloadAsync();
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });

        Assert.Equal(["title", "status", "subtitles", "audio", "size", "saved"], await HeadingsAsync(page));

        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns" }).ClickAsync();

        Assert.Equal(["title", "status", "audio", "subtitles", "size", "saved"], await HeadingsAsync(page));
        Assert.Equal(["select", "title", "status", "audio", "subtitles", "size", "saved"], await CellOrderAsync(page));

        await page.ReloadAsync();
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });

        Assert.Equal(["title", "status", "audio", "subtitles", "size", "saved"], await HeadingsAsync(page));
    }

    [E2EFact]
    public async Task A_narrow_table_hides_Audio_Subtitles_and_Back_and_a_moved_column_still_has_its_cells_under_its_heading()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig, Desktop);
        foreach (var name in new[] { "Audio", "Subtitles", "Back" })
        {
            await Expect(Heading(page, name)).ToBeVisibleAsync();
        }

        await page.SetViewportSizeAsync(Narrow.Width, Narrow.Height);

        foreach (var name in new[] { "Audio", "Subtitles", "Back" })
        {
            await Expect(Heading(page, name)).ToBeHiddenAsync();
        }

        foreach (var name in new[] { "Title", "What Weir would do", "Size" })
        {
            await Expect(Heading(page, name)).ToBeVisibleAsync();
        }

        await Heading(page, "Size").GetByRole(AriaRole.Button).FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowLeft");

        Assert.Equal(["title", "status", "size"], await VisibleHeadingsAsync(page));
        await AssertCellsUnderTheirHeadingsAsync(page, "Size", "size");
        await AssertCellsUnderTheirHeadingsAsync(page, "What Weir would do", "status");
        await AssertCellsUnderTheirHeadingsAsync(page, "Title", "title");

        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns" }).ClickAsync();

        Assert.Equal(["title", "status", "size"], await VisibleHeadingsAsync(page));
        await AssertCellsUnderTheirHeadingsAsync(page, "Size", "size");
    }

    [E2EFact]
    public async Task The_Files_card_header_keeps_every_control_inside_the_card_however_narrow_the_window_is()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig, Desktop);
        var card = page.GetByRole(AriaRole.Region, new() { Name = "Files" });
        var columns = card.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true });

        foreach (var width in new[] { 1100, 1000, Narrow.Width })
        {
            await page.SetViewportSizeAsync(width, Narrow.Height);

            await Expect(columns).ToBeVisibleAsync();
            var edge = await card.BoundingBoxAsync();
            var button = await columns.BoundingBoxAsync();
            Assert.True(button!.X + button.Width <= edge!.X + edge.Width, $"At {width} px the Columns button ends at {button.X + button.Width} but the card ends at {edge.X + edge.Width}.");
        }
    }

    private static ILocator Heading(IPage page, string name) => page.GetByRole(AriaRole.Columnheader, new() { Name = name, Exact = true });

    /// <summary>The columns the headings stand for, in the order they stand in, the box that selects a file left out.</summary>
    private static async Task<IReadOnlyList<string>> HeadingsAsync(IPage page) =>
        [.. (await page.GetByRole(AriaRole.Columnheader).EvaluateAllAsync<string[]>("heads => heads.map(head => head.dataset.col)")).Where(column => column != "select")];

    private static async Task<IReadOnlyList<string>> VisibleHeadingsAsync(IPage page) =>
        [.. (await page.GetByRole(AriaRole.Columnheader).EvaluateAllAsync<string[]>("heads => heads.filter(head => head.offsetParent !== null).map(head => head.dataset.col)")).Where(column => column != "select")];

    /// <summary>The columns of the first row, in the order its cells stand in.</summary>
    private static async Task<IReadOnlyList<string>> CellOrderAsync(IPage page) =>
        await page.GetByTestId("library-row").First.Locator("[data-col]").EvaluateAllAsync<string[]>("cells => cells.map(cell => cell.dataset.col)");

    /// <summary>The middle of the heading lies within the width of the first row's cell for that column.</summary>
    private static async Task AssertCellsUnderTheirHeadingsAsync(IPage page, string heading, string column)
    {
        var head = (await Heading(page, heading).BoundingBoxAsync())!;
        var cell = (await page.GetByTestId("library-row").First.Locator($"[data-col='{column}']").BoundingBoxAsync())!;
        var middle = head.X + (head.Width / 2);
        Assert.True(middle >= cell.X && middle <= cell.X + cell.Width, $"{heading} is at {head.X}..{head.X + head.Width} but its cell is at {cell.X}..{cell.X + cell.Width}.");
    }

    /// <summary>A workflow whose rules keep only English audio, with a film that needs cleaning and one that matches, scanned.</summary>
    private static async Task<LibraryRig> StartRigAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
            await rig.AddMatchingFilmAsync(Heat, "eng");
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            await rig.ScanAsync();
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<IPage> OpenLibraryAsync(LibraryRig rig, ViewportSize viewport)
    {
        var page = await NewPageAsync(viewport);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        return page;
    }
}
