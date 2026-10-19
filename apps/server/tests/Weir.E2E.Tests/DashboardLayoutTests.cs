using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Dashboard sizes itself to the room its main area has: wide, Live and System are exactly as tall as the window, the right column
/// sits beside the page and the page never scrolls; narrower the page flows and scrolls like any other, and on a phone the cards stack
/// in one column, the Pipeline's stations stack and the kind of work stays a three-button switch. The Pipeline is full, the busiest Live there is.
/// </summary>
public sealed class DashboardLayoutTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    private static readonly string[] LiveCards = ["Pipeline", "Health", "Needs you", "Just finished"];
    private static readonly string[] SystemCards = ["This Weir", "Storage", "Health", "Connections", "Scheduled tasks", "Backups and tools"];

    private async Task<ProcessingRig> StartBusyRigAsync()
    {
        var rig = await ProcessingRig.StartAsync();
        try
        {
            for (var number = 1; number <= 6; number++)
            {
                await rig.HandOffAsync($"h-lay-{number}", $"Film Number {number} 2024.mkv");
            }

            await rig.WaitUntilWorkingAsync("h-lay-1");
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private static async Task OpenAsync(IPage page, ProcessingRig rig, string view)
    {
        await page.GotoAsync($"{rig.BaseUrl}/?view={view}");
        await Expect(page.GetByTestId($"dashboard-{view}")).ToBeVisibleAsync(new() { Timeout = PushMs });
        if (view == "live")
        {
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Film Number 1", new() { Timeout = PushMs });
        }
        else
        {
            await Expect(page.GetByTestId("system-weir")).ToContainTextAsync("Version", new() { Timeout = PushMs });
        }
    }

    private static async Task<IReadOnlyDictionary<string, LayoutBox>> BoxesAsync(IPage page, string[] cards)
    {
        var boxes = new Dictionary<string, LayoutBox>();
        foreach (var card in cards)
        {
            var box = await page.GetByTestId("processing-page").GetByRole(AriaRole.Region, new() { Name = card, Exact = true }).First.BoundingBoxAsync();
            Assert.True(box is not null, $"The {card} card has no box.");
            boxes[card] = new LayoutBox(box.X, box.Y, box.Width, box.Height);
        }

        return boxes;
    }

    /// <summary>The card that fills the right column when there is room: Health on Live, Storage on System.</summary>
    private static string RightColumnCard(string view) => view == "live" ? "Health" : "Storage";

    private sealed record LayoutBox(double Left, double Top, double Width, double Height);

    private static async Task<(int InnerHeight, int ScrollHeight, int ScrollWidth, int ClientWidth)> ScrollSizesAsync(IPage page)
    {
        var sizes = await page.EvaluateAsync<int[]>("() => [window.innerHeight, document.documentElement.scrollHeight, document.documentElement.scrollWidth, document.documentElement.clientWidth]");
        return (sizes[0], sizes[1], sizes[2], sizes[3]);
    }

    [E2EFact]
    public async Task Wide_Live_and_System_are_exactly_window_high_with_the_right_column_beside_and_never_scroll()
    {
        await using var rig = await StartBusyRigAsync();
        foreach (var size in new[] { new ViewportSize { Width = 1440, Height = 900 }, new ViewportSize { Width = 1600, Height = 1000 }, new ViewportSize { Width = 1920, Height = 1080 } })
        {
            var page = await NewPageAsync(size);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            foreach (var (view, cards) in new[] { ("live", LiveCards), ("system", SystemCards) })
            {
                await OpenAsync(page, rig, view);

                var (inner, height, width, client) = await ScrollSizesAsync(page);
                var boxes = await BoxesAsync(page, cards);
                var beside = RightColumnCard(view);

                var where = $"{view} at {size.Width}x{size.Height}";
                Assert.True(height == inner, $"{where}: the page is {height} high in a {inner} window.");
                Assert.True(width <= client, $"{where}: the page is {width} wide in {client}.");
                Assert.True(boxes[beside].Left > boxes[cards[0]].Left + boxes[cards[0]].Width - 1, $"{where}: {beside} sits beside the page.");
                Assert.All(boxes.Values, box => Assert.True(box.Top + box.Height <= inner + 1, $"{where}: a card runs below the window."));
                await page.Mouse.WheelAsync(0, 600);
                Assert.Equal(0, await page.EvaluateAsync<int>("() => window.scrollY"));
            }
        }
    }

    [E2EFact]
    public async Task A_narrower_window_flows_with_the_right_column_below_and_the_page_scrolls()
    {
        await using var rig = await StartBusyRigAsync();
        var page = await NewPageAsync(new ViewportSize { Width = 1280, Height = 720 });
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        foreach (var (view, cards) in new[] { ("live", LiveCards), ("system", SystemCards) })
        {
            await OpenAsync(page, rig, view);

            var (inner, height, width, client) = await ScrollSizesAsync(page);
            var boxes = await BoxesAsync(page, cards);
            var beside = RightColumnCard(view);

            var where = $"{view} at 1280x720";
            Assert.True(height > inner, $"{where}: the page is {height} high in a {inner} window.");
            Assert.True(width <= client, $"{where}: the page is {width} wide in {client}.");
            Assert.True(boxes[beside].Left <= boxes[cards[0]].Left + 1, $"{where}: {beside} is below the page.");
            await page.Mouse.WheelAsync(0, 600);
            await page.WaitForFunctionAsync("() => window.scrollY > 0");
        }
    }

    [E2EFact]
    public async Task A_phone_stacks_every_card_in_one_column_the_width_of_the_window()
    {
        await using var rig = await StartBusyRigAsync();
        var page = await NewPageAsync(new ViewportSize { Width = 390, Height = 844 });
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        foreach (var (view, cards) in new[] { ("live", LiveCards), ("system", SystemCards) })
        {
            await OpenAsync(page, rig, view);
            if (view == "live")
            {
                await Expect(page.GetByTestId("live-filter")).ToBeVisibleAsync();
                await Expect(page.GetByTestId("live-filter-compact")).ToBeHiddenAsync();
                await Expect(page.GetByTestId("pipeline-board").Locator(".mm-pipe__body--stacked")).ToHaveCountAsync(1);
            }

            var (inner, height, width, client) = await ScrollSizesAsync(page);
            var boxes = await BoxesAsync(page, cards);

            var where = $"{view} at 390x844";
            Assert.True(height > inner, $"{where}: the page is {height} high in a {inner} window.");
            Assert.True(width <= client, $"{where}: the page is {width} wide in {client}.");
            var first = boxes[cards[0]];
            Assert.All(boxes.Values, box =>
            {
                Assert.True(Math.Abs(box.Left - first.Left) <= 1 && Math.Abs(box.Width - first.Width) <= 1, $"{where}: the cards share one column.");
            });
            await page.Mouse.WheelAsync(0, 600);
            await page.WaitForFunctionAsync("() => window.scrollY > 0");
        }
    }
}
