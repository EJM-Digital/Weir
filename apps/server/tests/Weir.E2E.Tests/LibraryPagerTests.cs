using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A library of more files than one page holds (a page is 200), read by a real server: Next and Previous turn the page and ask the
/// server for it, a selection belongs to the page it was made on, and a new search or another workflow starts at the first page again.
/// </summary>
public sealed class LibraryPagerTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const int PageSize = 200;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Needs = "Alpha (2000)/Alpha (2000).mkv";

    [E2EFact]
    public async Task Next_asks_the_server_for_the_second_page_and_drops_the_selection_and_Previous_goes_back()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var log = RequestLog.Watch(page);
        var range = page.GetByTestId("library-range");
        var pager = page.GetByTestId("library-pager");
        var next = pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true });
        var previous = pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true });
        await Expect(range).ToHaveTextAsync("Showing 1–200 of 201");
        await Expect(previous).ToBeDisabledAsync();
        await Expect(next).ToBeEnabledAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Select Alpha (2000)" }).CheckAsync();
        await Expect(page.GetByTestId("library-bulk")).ToContainTextAsync("1 selected");
        await Expect(pager).ToContainTextAsync("Your selection is for this page: moving to another page clears it.");
        var mark = log.ReadMark();

        await next.ClickAsync();

        await Expect(range).ToHaveTextAsync("Showing 201–201 of 201", new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1);
        await Expect(page.GetByTestId("library-row")).ToContainTextAsync("Film 200 (2001).mkv");
        await Expect(page.GetByTestId("library-bulk")).ToHaveCountAsync(0);
        await Expect(next).ToBeDisabledAsync();
        await Expect(previous).ToBeEnabledAsync();
        var asked = log.ReadsSince(mark).Single(read => read.PathAndQuery.Contains("/library-files?", StringComparison.Ordinal) && read.PathAndQuery.Contains("page=2", StringComparison.Ordinal));
        Assert.Contains($"page_size={PageSize}", asked.PathAndQuery, StringComparison.Ordinal);
        var served = await rig.FilesAsync(("page", 2), ("page_size", PageSize), ("sort", "title"), ("direction", "asc"));
        Assert.Equal(201, (int)served.Fields["total"]!);
        Assert.Single(served.Fields["files"]!.AsArray());

        await previous.ClickAsync();

        await Expect(range).ToHaveTextAsync("Showing 1–200 of 201", new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(PageSize);
    }

    [E2EFact]
    public async Task A_new_search_starts_at_the_first_page_with_the_search_asked_of_the_server_and_a_search_with_no_match_hides_the_pager()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var log = RequestLog.Watch(page);
        var pager = page.GetByTestId("library-pager");
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this workflow" });
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Select Alpha (2000)" }).CheckAsync();
        await Expect(page.GetByTestId("library-bulk")).ToContainTextAsync("1 selected");

        await search.FillAsync("film 19");

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(10, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-bulk")).ToHaveCountAsync(0);

        await search.FillAsync(string.Empty);
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(PageSize, new() { Timeout = ServerMs });
        await pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("library-range")).ToHaveTextAsync("Showing 201–201 of 201", new() { Timeout = ServerMs });
        var mark = log.ReadMark();

        await search.FillAsync("alpha");

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToContainTextAsync("Alpha (2000).mkv");
        await Expect(pager).ToHaveCountAsync(0);
        var asked = log.ReadsSince(mark).Last(read => read.PathAndQuery.Contains("/library-files?", StringComparison.Ordinal));
        Assert.Contains("q=alpha", asked.PathAndQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("page=2", asked.PathAndQuery, StringComparison.Ordinal);

        await search.FillAsync("zzz");

        await Expect(page.GetByTestId("library-empty")).ToContainTextAsync("Nothing here matches what you asked for.", new() { Timeout = ServerMs });
        await Expect(pager).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Choosing_another_workflow_from_the_second_page_starts_each_workflow_at_its_first_page()
    {
        await using var rig = await StartRigAsync();
        var anime = await rig.AddWorkflowAsync("Anime");
        await LibraryRig.AddFilmToAsync(anime, "Akira (1988)/Akira (1988).mkv", "jpn");
        await rig.ScanAsync(anime.Id);
        var page = await OpenLibraryAsync(rig);
        await page.GetByTestId("library-pager").GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("library-range")).ToHaveTextAsync("Showing 201–201 of 201", new() { Timeout = ServerMs });
        var trigger = page.GetByTestId("library-picker").GetByRole(AriaRole.Button).First;
        var list = page.GetByRole(AriaRole.Listbox, new() { Name = "Choose a workflow" });

        await trigger.ClickAsync();
        await list.GetByRole(AriaRole.Option, new() { NameRegex = new("^Anime") }).ClickAsync();

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-pager")).ToHaveCountAsync(0);

        await trigger.ClickAsync();
        await list.GetByRole(AriaRole.Option, new() { NameRegex = new("^E2E library films") }).ClickAsync();

        await Expect(page.GetByTestId("library-range")).ToHaveTextAsync("Showing 1–200 of 201", new() { Timeout = ServerMs });
        await Expect(page).ToHaveURLAsync(new Regex($"[?&]library={rig.LibraryId}"));
    }

    /// <summary>201 films: one that needs cleaning, which sorts first, and 200 that already match, so the last of them is alone on the second page.</summary>
    private static async Task<LibraryRig> StartRigAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.AddFilmAsync(Needs, ["eng", "fre"], []);
            for (var number = 1; number <= PageSize; number++)
            {
                var title = $"Film {number:000} (2001)";
                await rig.AddMatchingFilmAsync($"{title}/{title}.mkv", "eng");
            }

            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            await rig.ScanAsync();
            Assert.Equal(PageSize + 1, (int)(await rig.FilesAsync()).Fields["total"]!);
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<IPage> OpenLibraryAsync(LibraryRig rig)
    {
        var page = await NewPageAsync(Desktop);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(PageSize, new() { Timeout = ServerMs });
        return page;
    }
}
