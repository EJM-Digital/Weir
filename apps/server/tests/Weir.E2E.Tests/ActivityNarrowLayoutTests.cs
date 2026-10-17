using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// How Activity gives up room in a real browser as the window narrows: with room to spare everything sits on the title line; short of room
/// the search shrinks to a mark and the two pickers move into the Files card, and the last kind-of-file chips fold into a More menu from which any
/// of them can still be chosen, the chosen one staying on the line; on a phone the chips fold and the pickers sit on their own row.
/// </summary>
public sealed class ActivityNarrowLayoutTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private static readonly ViewportSize Wide = new() { Width = 2560, Height = 1000 };
    private static readonly ViewportSize Short = new() { Width = 1500, Height = 900 };
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 844 };

    [E2EFact]
    public async Task With_room_to_spare_the_search_the_pickers_and_every_chip_sit_on_the_title_line()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenActivityAsync(rig, Wide);

        await Expect(Search(page)).ToBeVisibleAsync();
        await Expect(Search(page).Locator("xpath=ancestor::label")).ToHaveAttributeAsync("data-collapsed", "false");
        await Expect(HeaderPicker(page, "Workflow")).ToBeVisibleAsync();
        await Expect(HeaderPicker(page, "How far back")).ToBeVisibleAsync();
        await Expect(CardPicker(page, "Workflow")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new("^More") })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new("^Kept") })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Short_of_room_the_search_is_a_mark_the_pickers_move_into_the_Files_card_and_the_last_chips_fold_into_More_where_one_can_still_be_chosen()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenActivityAsync(rig, Short);
        var group = page.GetByRole(AriaRole.Group, new() { Name = "Show" });

        await Expect(Search(page).Locator("xpath=ancestor::label")).ToHaveAttributeAsync("data-collapsed", "true");
        await Expect(CardPicker(page, "Workflow")).ToBeVisibleAsync();
        await Expect(CardPicker(page, "How far back")).ToBeVisibleAsync();
        await Expect(HeaderPicker(page, "Workflow")).ToHaveCountAsync(0);
        await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Kept") })).ToHaveCountAsync(0);
        var more = group.GetByRole(AriaRole.Button, new() { NameRegex = new("^More") });
        await more.ClickAsync();
        var menu = page.GetByRole(AriaRole.Menu, new() { Name = "More kinds of file" });
        await Expect(menu).ToBeVisibleAsync();
        await menu.GetByRole(AriaRole.Menuitem).Last.ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=kept(&|$)"), new() { Timeout = PushMs });
        await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Kept") })).ToHaveAttributeAsync("aria-pressed", "true");

        await CardPicker(page, "How far back").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Today", Exact = true }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]within=1(&|$)"), new() { Timeout = PushMs });
        await Expect(CardPicker(page, "How far back")).ToContainTextAsync("Today");
    }

    [E2EFact]
    public async Task On_a_phone_the_last_chips_fold_into_More_and_the_search_and_pickers_are_still_there_to_use()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenActivityAsync(rig, Phone);
        var group = page.GetByRole(AriaRole.Group, new() { Name = "Show" });
        var more = group.GetByRole(AriaRole.Button, new() { NameRegex = new("^More") });

        await Expect(more).ToBeVisibleAsync();
        await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Kept") })).ToHaveCountAsync(0);
        await more.ClickAsync();
        var menu = page.GetByRole(AriaRole.Menu, new() { Name = "More kinds of file" });
        await Expect(menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = new("^Kept") })).ToBeVisibleAsync();
        await menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = new("^Kept") }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=kept(&|$)"), new() { Timeout = PushMs });
        await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Kept") })).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(HeaderPicker(page, "Workflow")).ToBeVisibleAsync();
        await Expect(HeaderPicker(page, "How far back")).ToBeVisibleAsync();
        await Search(page).FillAsync("nothing");
        await Search(page).PressAsync("Enter");
        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]q=nothing(&|$)"), new() { Timeout = PushMs });
    }

    private static ILocator Search(IPage page) => page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a file" });

    private static ILocator HeaderPicker(IPage page, string name) =>
        page.GetByTestId("activity-filters").GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static ILocator CardPicker(IPage page, string name) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private async Task<IPage> OpenActivityAsync(ProcessingRig rig, ViewportSize viewport)
    {
        var page = await NewPageAsync(viewport);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(page.GetByTestId("activity-filters")).ToBeVisibleAsync(new() { Timeout = PushMs });
        return page;
    }
}
