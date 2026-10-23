using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A card on the Pipeline gives its title two lines when the board has the height for them and one when it does not. The board measures
/// the room it has, so the same card changes as the window is dragged shorter and taller, both where the stations sit side by side and
/// where they stack, and the title reads in full in the card's tooltip whatever it shows.
/// </summary>
public sealed class PipelineTitleLinesTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const string FileName = "The Extraordinarily Long Title Of A Film That Wraps Twice 2024.mkv";

    // How many lines the first card's title is drawn on, read from the title's own height and line height.
    private const string TitleLines = """
        (() => {
          const title = document.querySelector('[data-testid="pipeline-board"] .mm-pipe__title');
          if (!title) return 0;
          return Math.round(title.getBoundingClientRect().height / parseFloat(getComputedStyle(title).lineHeight));
        })()
        """;

    private static readonly ViewportSize Tall = new() { Width = 1600, Height = 1100 };
    private static readonly ViewportSize Short = new() { Width = 1600, Height = 520 };
    private static readonly ViewportSize TallPhone = new() { Width = 700, Height = 1100 };
    private static readonly ViewportSize ShortPhone = new() { Width = 700, Height = 520 };

    private async Task<(ProcessingRig Rig, IPage Page)> OpenWithAFileAsync(ViewportSize viewport)
    {
        var rig = await ProcessingRig.StartAsync();
        try
        {
            await rig.HandOffAsync("h-title", FileName);
            await rig.WaitUntilWorkingAsync("h-title");
            var page = await NewPageAsync(viewport);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await Expect(page.GetByTestId("pipeline-board").Locator(".mm-pipe__title")).ToBeVisibleAsync(new() { Timeout = PushMs });
            return (rig, page);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private static async Task ExpectTitleLinesAsync(IPage page, int lines) =>
        await page.WaitForFunctionAsync($"() => {TitleLines} === {lines}", null, new() { Timeout = Navigation.UrlAssertMs });

    [E2EFact]
    public async Task A_title_takes_two_lines_in_a_tall_window_and_one_in_a_short_one_as_the_window_is_dragged_beside_the_stations()
    {
        var (rig, page) = await OpenWithAFileAsync(Tall);
        await using var _ = rig;
        await ExpectTitleLinesAsync(page, 2);

        await page.SetViewportSizeAsync(Short.Width, Short.Height);
        await ExpectTitleLinesAsync(page, 1);

        await page.SetViewportSizeAsync(Tall.Width, Tall.Height);
        await ExpectTitleLinesAsync(page, 2);
    }

    [E2EFact]
    public async Task A_title_takes_two_lines_in_a_tall_window_and_one_in_a_short_one_where_the_stations_stack()
    {
        var (rig, page) = await OpenWithAFileAsync(TallPhone);
        await using var _ = rig;
        await ExpectTitleLinesAsync(page, 2);

        await page.SetViewportSizeAsync(ShortPhone.Width, ShortPhone.Height);
        await ExpectTitleLinesAsync(page, 1);

        await page.SetViewportSizeAsync(TallPhone.Width, TallPhone.Height);
        await ExpectTitleLinesAsync(page, 2);
    }

    [E2EFact]
    public async Task A_card_keeps_its_whole_title_for_the_tooltip_when_the_window_leaves_room_for_one_line()
    {
        var (rig, page) = await OpenWithAFileAsync(Short);
        await using var _ = rig;
        await ExpectTitleLinesAsync(page, 1);

        var card = page.GetByTestId("pipeline-board").GetByRole(AriaRole.Button).First;

        await Expect(card).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("^The Extraordinarily Long Title Of A Film That Wraps Twice"));
    }
}
