using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A film's panel on the Library page in a real browser: focus moves into it, Tab stays inside, Escape, the cross and a click beside it
/// close it with focus back on the film, and "Clean this file" asks first, cleans by the workflow's own rules when confirmed, and says
/// what it did, which the server and the disk bear out.
/// </summary>
public sealed class LibraryFileDrawerTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const float CleanMs = 180_000;
    private const int TabStops = 40;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";

    private const string InsideDrawer = "() => document.querySelector('[data-testid=\"library-file-drawer\"]')?.contains(document.activeElement) === true";

    [E2EFact]
    public async Task Focus_moves_into_the_panel_Tab_and_Shift_Tab_stay_inside_it_and_Escape_closes_it_with_focus_back_on_the_film()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var opener = page.GetByRole(AriaRole.Button, new() { NameRegex = new("Arrival") }).First;
        var drawer = page.GetByTestId("library-file-drawer");

        await opener.ClickAsync();

        await Expect(drawer).ToBeVisibleAsync();
        await Expect(drawer).ToHaveAttributeAsync("aria-modal", "true");
        Assert.True(await page.EvaluateAsync<bool>(InsideDrawer), "Focus did not move into the panel.");
        for (var stop = 0; stop < TabStops; stop++)
        {
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await page.EvaluateAsync<bool>(InsideDrawer), $"Tab number {stop + 1} left the panel.");
        }

        for (var stop = 0; stop < TabStops; stop++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.True(await page.EvaluateAsync<bool>(InsideDrawer), $"Shift+Tab number {stop + 1} left the panel.");
        }

        await page.Keyboard.PressAsync("Escape");

        await Expect(drawer).ToHaveCountAsync(0);
        await Expect(opener).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task The_cross_and_a_click_beside_the_panel_close_it_and_it_opens_again_for_another_film()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var drawer = page.GetByTestId("library-file-drawer");

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Arrival") }).First.ClickAsync();
        await Expect(drawer).ToBeVisibleAsync();
        await drawer.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();

        await Expect(drawer).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Heat") }).First.ClickAsync();
        await Expect(drawer).ToContainTextAsync("Heat (1995).mkv");
        await page.Locator(".mm-drawer-backdrop").ClickAsync(new() { Position = new() { X = 700, Y = 450 } });

        await Expect(drawer).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("library-confirm")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Clean_this_file_asks_and_on_Yes_cleans_by_the_workflows_rules_with_no_tracks_chosen_and_the_panel_says_what_it_did()
    {
        await using var rig = await StartRigAsync();
        var page = await OpenLibraryAsync(rig);
        var log = RequestLog.Watch(page);
        var film = Path.Combine(rig.LibraryFolder, Arrival);
        var before = LibraryRig.HashOf(film);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Arrival") }).First.ClickAsync();
        var drawer = page.GetByTestId("library-file-drawer");
        var clean = drawer.GetByRole(AriaRole.Button, new() { Name = "Clean this file", Exact = true });
        await Expect(clean).ToBeEnabledAsync(new() { Timeout = ServerMs });
        await clean.ClickAsync();
        var dialog = page.GetByTestId("library-confirm");
        await Expect(dialog).ToContainTextAsync("Clean this file?", new() { Timeout = ServerMs });
        Assert.Equal(before, LibraryRig.HashOf(film));
        var mark = log.Mark();

        await dialog.GetByTestId("library-confirm-confirm").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        await Expect(drawer.GetByTestId("library-outcome")).ToBeVisibleAsync(new() { Timeout = CleanMs });
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-files/clean", sent.PathAndQuery, StringComparison.Ordinal);
        var body = JsonNode.Parse(sent.Body!)!;
        Assert.True((bool)body["confirm_final_removal"]!);
        Assert.Null(body["manual"]);
        Assert.Single(body["paths"]!.AsArray());
        await Poll.UntilAsync(async () => (await rig.CleansAsync()).Count == 1, "the clean to finish", TimeSpan.FromMilliseconds(CleanMs));
        Assert.NotEqual(before, LibraryRig.HashOf(film));
        await Poll.UntilAsync(async () => (string)(await rig.FileRowAsync(film))["status"]! == "matches", "the cleaned film to read as matching", TimeSpan.FromMilliseconds(CleanMs));
        await Expect(drawer.GetByRole(AriaRole.Button, new() { Name = "Clean this file", Exact = true })).ToBeDisabledAsync(new() { Timeout = ServerMs });
    }

    /// <summary>A workflow whose rules keep only English audio, with a film that has French too (it needs cleaning) and one that matches, scanned.</summary>
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

    private async Task<IPage> OpenLibraryAsync(LibraryRig rig)
    {
        var page = await NewPageAsync(Desktop);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        return page;
    }
}
