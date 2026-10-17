using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Library page's own controls in a real browser, against a library of three films read by a real server: a status chip, the
/// search, a heading to sort by, and Left alone in a film's panel. What the page lists is read against what the server says, and Left
/// alone is read back from the server.
/// </summary>
public sealed partial class LibraryControlsTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";
    private const string Zodiac = "Zodiac (2007)/Zodiac (2007).mkv";

    [GeneratedRegex(@"^Needs cleaning\s*1")]
    private static partial Regex OneNeedsCleaning();

    [GeneratedRegex(@"^Needs cleaning\s*0")]
    private static partial Regex NoneNeedCleaning();

    [GeneratedRegex(@"^Left alone\s*1")]
    private static partial Regex OneLeftAlone();

    [GeneratedRegex(@"^Left alone\s*0")]
    private static partial Regex NoneLeftAlone();

    [E2EFact]
    public async Task A_status_chip_lists_only_its_films_and_puts_the_choice_in_the_address_and_choosing_it_again_lists_them_all()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var rows = page.GetByTestId("library-row");
        var needsCleaning = Chip(page, new Regex(@"^Needs cleaning"));

        await needsCleaning.ClickAsync();

        await Expect(rows).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(rows).ToContainTextAsync("Arrival (2016).mkv");
        await Expect(needsCleaning).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=needs_cleaning"), new() { Timeout = ServerMs });
        Assert.Equal(["Arrival (2016).mkv"], await FileNamesAsync(rig, ("status", "needs_cleaning")));

        await Chip(page, new Regex(@"^Matches rules")).ClickAsync();

        await Expect(rows).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=matches"), new() { Timeout = ServerMs });
        Assert.Equal(["Heat (1995).mkv", "Zodiac (2007).mkv"], await FileNamesAsync(rig, ("status", "matches")));

        await Chip(page, new Regex(@"^Matches rules")).ClickAsync();

        await Expect(rows).ToHaveCountAsync(3, new() { Timeout = ServerMs });
        await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]show="), new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task Typing_in_the_search_lists_only_the_films_that_match_and_clearing_it_lists_them_all()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var rows = page.GetByTestId("library-row");
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this workflow" });

        await search.FillAsync("heat");

        await Expect(rows).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(rows).ToContainTextAsync("Heat (1995).mkv");
        Assert.Equal(["Heat (1995).mkv"], await FileNamesAsync(rig, ("q", "heat")));

        await search.FillAsync(string.Empty);

        await Expect(rows).ToHaveCountAsync(3, new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task A_heading_sorts_the_whole_library_and_a_second_click_reverses_it_in_the_order_the_server_gives()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var title = page.GetByRole(AriaRole.Columnheader, new() { Name = "Title", Exact = true });
        await Expect(title).ToHaveAttributeAsync("aria-sort", "ascending");
        Assert.Equal(await FileNamesAsync(rig, ("sort", "title"), ("direction", "asc")), await ListedNamesAsync(page));

        await title.GetByRole(AriaRole.Button).ClickAsync();

        await Expect(title).ToHaveAttributeAsync("aria-sort", "descending");
        var descending = await FileNamesAsync(rig, ("sort", "title"), ("direction", "desc"));
        Assert.Equal(["Zodiac (2007).mkv", "Heat (1995).mkv", "Arrival (2016).mkv"], descending);
        await Expect(page.GetByTestId("library-row").First).ToContainTextAsync("Zodiac (2007).mkv");
        Assert.Equal(descending, await ListedNamesAsync(page));

        await title.GetByRole(AriaRole.Button).ClickAsync();

        await Expect(title).ToHaveAttributeAsync("aria-sort", "ascending");
        await Expect(page.GetByTestId("library-row").First).ToContainTextAsync("Arrival (2016).mkv");
    }

    [E2EFact]
    public async Task Left_alone_in_a_films_panel_is_saved_by_the_server_stops_the_film_being_cleaned_and_can_be_taken_back()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var log = RequestLog.Watch(page);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Arrival") }).First.ClickAsync();
        var drawer = page.GetByTestId("library-file-drawer");
        await Expect(drawer).ToBeVisibleAsync();
        var leftAlone = drawer.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Left alone") });
        var clean = drawer.GetByRole(AriaRole.Button, new() { Name = "Clean this file", Exact = true });
        await Expect(clean).ToBeEnabledAsync(new() { Timeout = ServerMs });
        var arrival = Path.Combine(rig.LibraryFolder, Arrival);
        var mark = log.Mark();

        await leftAlone.ClickAsync();
        await Expect(leftAlone).ToBeCheckedAsync();

        await Expect(drawer.GetByTestId("library-leave-alone-status")).ToContainTextAsync("Saved. Weir will leave this file alone.", new() { Timeout = ServerMs });
        var row = await rig.FileRowAsync(arrival);
        Assert.True((bool)row["leave_alone"]!);
        Assert.Equal("left_alone", (string)row["status"]!);
        var sent = Assert.Single(log.Since(mark));
        Assert.EndsWith("/library-files/leave-alone", sent.PathAndQuery, StringComparison.Ordinal);
        await Expect(clean).ToBeDisabledAsync();
        await Expect(Chip(page, OneLeftAlone())).ToBeVisibleAsync(new() { Timeout = ServerMs });
        await Expect(Chip(page, NoneNeedCleaning())).ToBeVisibleAsync(new() { Timeout = ServerMs });

        await leftAlone.ClickAsync();
        await Expect(leftAlone).Not.ToBeCheckedAsync();

        await Expect(drawer.GetByTestId("library-leave-alone-status")).ToContainTextAsync("Saved. This file can be cleaned again.", new() { Timeout = ServerMs });
        Assert.False((bool)(await rig.FileRowAsync(arrival))["leave_alone"]!);
        await Expect(clean).ToBeEnabledAsync();
        await Expect(Chip(page, NoneLeftAlone())).ToBeVisibleAsync(new() { Timeout = ServerMs });
        await Expect(Chip(page, OneNeedsCleaning())).ToBeVisibleAsync(new() { Timeout = ServerMs });
        Assert.Equal(0, await rig.CleanJobCountAsync());
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    /// <summary>A workflow whose rules keep only English audio, with a film that has French too (it needs cleaning) and two that match, all scanned.</summary>
    private static async Task<LibraryRig> StartRigAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
            await rig.AddMatchingFilmAsync(Heat, "eng");
            await rig.AddMatchingFilmAsync(Zodiac, "eng");
            await rig.ScanAsync();
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
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(3, new() { Timeout = ServerMs });
        return page;
    }

    private static ILocator Chip(IPage page, Regex name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = name });

    /// <summary>The file names in the order the server lists the library when it is asked for <paramref name="query"/>.</summary>
    private static async Task<IReadOnlyList<string>> FileNamesAsync(LibraryRig rig, params (string Name, object Value)[] query) =>
        [.. (await rig.FilesAsync(query)).Fields["files"]!.AsArray().Select(file => Path.GetFileName((string)file!["path"]!))];

    /// <summary>The file names in the order the Files table lists them.</summary>
    private static async Task<IReadOnlyList<string>> ListedNamesAsync(IPage page)
    {
        var rows = await page.GetByTestId("library-row").AllInnerTextsAsync();
        return [.. rows.Select(text => Regex.Match(text, @"[^\n/]+\.mkv").Value)];
    }
}
