using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs says how much of the log it holds: the count over the list, how many are shown when there are more, and the older pages
/// the cursor asks for. The events it counts are written into a server of the test's own, as another process of Weir's would, before the page reads them.
/// </summary>
public sealed class SystemLogSummaryTests(E2EServer server) : E2ETestBase(server)
{
    private const string Log = "/api/v1/system/log";
    private const string Marker = "summary-marker";
    private const int Marked = 120;
    private const int PageSize = 50;
    private const int MostPages = 12;

    private static void WriteMarkedEvents(string databasePath, int count)
    {
        for (var number = 1; number <= count; number++)
        {
            E2EDatabase.InsertActivityEvent(databasePath, "auth.password_changed", "auth", $"{Marker} {number}", $"{Marker} {number} detail");
        }
    }

    private static ILocator Summary(IPage page) => page.GetByTestId("log-summary");

    private static ILocator Older(IPage page) => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Load older entries|Loading older)") });

    [E2EFact]
    public async Task The_summary_names_how_many_entries_match_how_many_are_shown_and_older_pages_are_asked_for_by_cursor()
    {
        var opened = await NewPageAsync();
        await using var system = await SystemSession.OpenAsync(opened, "Backups");
        var page = system.Page;
        WriteMarkedEvents(system.Server.DatabasePath, Marked);
        var reads = ReadsSent.Watch(page);
        var firstPage = await system.ReadAsync($"{Log}?q={Marker}&limit={PageSize}");
        Assert.Equal(Marked, (int)firstPage["total"]!);
        var cursor = (string)firstPage["next_cursor"]!;

        await page.GotoAsync($"{system.Server.BaseUrl.GetLeftPart(UriPartial.Authority)}/system?tab=logs&q={Marker}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(Summary(page)).ToHaveTextAsync($"Showing {PageSize} of {Marked} entries · live");
        await Expect(page.GetByTestId("log-feed").GetByTestId("log-row")).ToHaveCountAsync(PageSize);

        var mark = reads.Mark();
        await Older(page).ClickAsync();

        await Expect(Summary(page)).ToHaveTextAsync($"Showing {2 * PageSize} of {Marked} entries · live");
        var asked = Assert.Single(reads.Since(mark, Log));
        Assert.Equal(cursor, asked.Parameters["cursor"]);
        Assert.Equal(PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), asked.Parameters["limit"]);
        Assert.Equal(Marker, asked.Parameters["q"]);

        await Older(page).ClickAsync();

        await Expect(Summary(page)).ToHaveTextAsync($"{Marked} entries matching · live");
        await Expect(Older(page)).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("log-feed").GetByTestId("log-row")).ToHaveCountAsync(Marked);
    }

    [E2EFact]
    public async Task One_match_reads_in_the_singular_and_everything_loaded_reads_as_a_plain_count()
    {
        var opened = await NewPageAsync();
        await using var system = await SystemSession.OpenAsync(opened, "Backups");
        var page = system.Page;
        WriteMarkedEvents(system.Server.DatabasePath, Marked);
        E2EDatabase.InsertActivityEvent(system.Server.DatabasePath, "auth.password_changed", "auth", "only-one-of-these", "only-one-of-these detail");
        var baseUrl = system.Server.BaseUrl.GetLeftPart(UriPartial.Authority);

        await page.GotoAsync($"{baseUrl}/system?tab=logs&q=only-one-of-these", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(Summary(page)).ToHaveTextAsync("1 entry matching · live");

        await page.GotoAsync($"{baseUrl}/system?tab=logs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var total = (int)(await system.ReadAsync(Log))["total"]!;
        await Expect(Summary(page)).ToHaveTextAsync($"Showing {PageSize} of {total} entries · live");
        for (var pages = 0; pages < MostPages && await Older(page).CountAsync() > 0; pages++)
        {
            var shown = await page.GetByTestId("log-feed").GetByTestId("log-row").CountAsync();
            await Older(page).ClickAsync();
            await Expect(page.GetByTestId("log-feed").GetByTestId("log-row")).Not.ToHaveCountAsync(shown);
        }

        await Expect(Summary(page)).ToHaveTextAsync($"{total} entries · live");
    }
}
