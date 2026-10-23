using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>The Today chart from the keyboard: the arrow keys, Home and End move along it, and any other key leaves it where it is.</summary>
public sealed class DashboardTodayChartKeysTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task The_arrow_keys_Home_and_End_move_along_the_chart_and_other_keys_leave_it_where_it_is()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var chart = page.GetByRole(AriaRole.Slider, new() { Name = "Files finished, five minutes to a point", Exact = true });
        await chart.FocusAsync();
        var last = await chart.GetAttributeAsync("aria-valuemax");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", last!);

        await chart.PressAsync("a");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", last!);

        await chart.PressAsync("ArrowLeft");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", (int.Parse(last!) - 1).ToString());
        await chart.PressAsync("Home");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", "0");
        await chart.PressAsync("ArrowRight");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", "1");
        await chart.PressAsync("End");
        await Expect(chart).ToHaveAttributeAsync("aria-valuenow", last!);
    }
}
