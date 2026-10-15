using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The whole of cleaning in the Library page, in a real browser: tick a film, Clean, confirm, and watch the film go from "Needs cleaning"
/// to "Cleaning" to "Matches" with its counts following, and no reload. The server cleans a real file (the fake ffmpeg rewrites it), and
/// what the page shows is checked against what the server and the disk hold.
/// </summary>
public sealed partial class LibraryCleanLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const float CleanMs = 180_000;
    private const string Film = "Arrival (2016)/Arrival (2016).mkv";

    [GeneratedRegex(@"^Needs cleaning\s*1")]
    private static partial Regex OneNeedsCleaning();

    [GeneratedRegex(@"^Needs cleaning\s*0")]
    private static partial Regex NoneNeedCleaning();

    [GeneratedRegex(@"^Cleaning\s*1")]
    private static partial Regex OneCleaning();

    [GeneratedRegex(@"^Cleaning\s*0")]
    private static partial Regex NoneCleaning();

    [GeneratedRegex(@"^Matches rules\s*1")]
    private static partial Regex OneMatches();

    [E2EFact]
    public async Task A_ticked_film_cleaned_through_the_page_goes_from_Needs_cleaning_to_Cleaning_to_Matches_with_its_counts_and_no_reload()
    {
        await using var rig = await LibraryRig.StartAsync();
        await rig.UseRulesKeepingAudioAsync("English only", "eng");
        var film = await rig.AddFilmAsync(Film, ["eng", "fre"], []);
        await rig.ScanAsync();
        var scanned = LibraryRig.HashOf(film);
        rig.HoldClean(film);
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var row = page.GetByTestId("library-row");
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(row).ToContainTextAsync("1 audio");
        await Expect(Chip(page, OneNeedsCleaning())).ToBeVisibleAsync();
        await Expect(Chip(page, NoneCleaning())).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Select ") }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Clean these files", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("library-confirm");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = ServerMs });
        var mark = log.Mark();
        await dialog.GetByTestId("library-confirm-confirm").ClickAsync();

        await Expect(row).ToContainTextAsync("Cleaning now", new() { Timeout = ServerMs });
        await Expect(Chip(page, OneCleaning())).ToBeVisibleAsync();
        await Expect(Chip(page, NoneNeedCleaning())).ToBeVisibleAsync();
        await Expect(dialog).ToBeHiddenAsync();
        Assert.Equal("cleaning", (string)(await rig.FileRowAsync(film))["status"]!);
        Assert.Equal(scanned, LibraryRig.HashOf(film));
        var sent = Assert.Single(log.Since(mark));
        Assert.EndsWith("/library-files/clean", sent.PathAndQuery, StringComparison.Ordinal);

        rig.ReleaseClean(film);

        await Expect(row).ToContainTextAsync("Matches your rules", new() { Timeout = CleanMs });
        await Expect(Chip(page, OneMatches())).ToBeVisibleAsync();
        await Expect(Chip(page, NoneCleaning())).ToBeVisibleAsync();
        Assert.Equal("matches", (string)(await rig.FileRowAsync(film))["status"]!);
        Assert.Single(await rig.CleansAsync());
        Assert.NotEqual(scanned, LibraryRig.HashOf(film));
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    private static ILocator Chip(IPage page, Regex name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = name });
}
