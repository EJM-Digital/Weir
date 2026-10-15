using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Cleaning library files removes tracks for good, so the Library page asks first. Leaving that question any way but "Yes" (Not now,
/// Escape) must clean nothing, and clicking beside it must leave it open; the only request the page may send is the one that asks
/// what the clean would remove. Each test reads back from the server and the disk that nothing was queued or rewritten.
/// </summary>
public sealed class LibraryCleanConfirmationTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const float CleanMs = 180_000;
    private const string Film = "Arrival (2016)/Arrival (2016).mkv";

    [E2EFact]
    public async Task Not_now_on_the_question_for_a_selection_cleans_nothing_and_keeps_the_selection()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await SelectFilmAsync(page);
        var dialog = await AskToCleanSelectionAsync(page, log);
        var mark = log.Mark();

        await dialog.GetByTestId("library-confirm-cancel").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.GetByTestId("library-bulk")).ToContainTextAsync("1 selected");
        await AssertNothingCleanedAsync(rig, log, mark, open);
    }

    [E2EFact]
    public async Task Escape_on_the_question_for_a_selection_cleans_nothing()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await SelectFilmAsync(page);
        var dialog = await AskToCleanSelectionAsync(page, log);
        var mark = log.Mark();

        await page.Keyboard.PressAsync("Escape");

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingCleanedAsync(rig, log, mark, open);
    }

    [E2EFact]
    public async Task A_click_beside_the_question_leaves_it_open_and_cleans_nothing()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await SelectFilmAsync(page);
        var dialog = await AskToCleanSelectionAsync(page, log);
        var mark = log.Mark();

        await page.Mouse.ClickAsync(2, 2);

        await Expect(dialog).ToBeVisibleAsync();
        await AssertNothingCleanedAsync(rig, log, mark, open);
        await dialog.GetByTestId("library-confirm-cancel").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingCleanedAsync(rig, log, mark, open);
    }

    [E2EFact]
    public async Task Not_now_on_the_question_for_one_file_from_its_panel_cleans_nothing()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Arrival") }).First.ClickAsync();
        var mark = log.Mark();
        await page.GetByRole(AriaRole.Button, new() { Name = "Clean this file", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("library-confirm");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = ServerMs });
        await Expect(dialog).ToContainTextAsync("Clean this file?");
        AssertOnlyAskedWhatTheCleanWouldRemove(Sent(log, mark));
        var asked = log.Mark();

        await dialog.GetByTestId("library-confirm-cancel").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingCleanedAsync(rig, log, asked, open);
    }

    [E2EFact]
    public async Task Clear_on_the_selection_bar_empties_the_selection_and_sends_nothing()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await SelectFilmAsync(page);
        var mark = log.Mark();

        await page.GetByTestId("library-bulk").GetByRole(AriaRole.Button, new() { Name = "Clear", Exact = true }).ClickAsync();

        await Expect(page.GetByTestId("library-bulk")).ToHaveCountAsync(0);
        await AssertNothingCleanedAsync(rig, log, mark, open);
    }

    [E2EFact]
    public async Task Yes_cleans_the_selection_once_with_the_confirmation_and_the_selection_is_empty_afterwards()
    {
        await using var rig = await LibraryRig.StartAsync();
        var open = await OpenLibraryAsync(rig);
        var (page, log) = (open.Page, open.Log);
        await SelectFilmAsync(page);
        var dialog = await AskToCleanSelectionAsync(page, log);
        var mark = log.Mark();

        await dialog.GetByTestId("library-confirm-confirm").ClickAsync();

        var outcome = page.GetByTestId("library-outcome");
        await Expect(outcome).ToBeVisibleAsync(new() { Timeout = ServerMs });
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.GetByTestId("library-bulk")).ToHaveCountAsync(0);
        var sent = Assert.Single(Sent(log, mark));
        Assert.Equal("POST", sent.Method);
        Assert.EndsWith("/library-files/clean", sent.PathAndQuery, StringComparison.Ordinal);
        Assert.True((bool)JsonNode.Parse(sent.Body!)!["confirm_final_removal"]!);
        await Poll.UntilAsync(async () => (await rig.CleansAsync()).Count == 1, "the clean to finish", TimeSpan.FromMilliseconds(CleanMs));
        Assert.NotEqual(open.Hash, LibraryRig.HashOf(open.Film));

        await outcome.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();

        await Expect(outcome).ToHaveCountAsync(0);
    }

    /// <summary>A workflow whose rules keep only English audio, a film with English and French audio in it, and that film scanned.</summary>
    private async Task<OpenLibrary> OpenLibraryAsync(LibraryRig rig)
    {
        await rig.UseRulesKeepingAudioAsync("English only", "eng");
        var film = await rig.AddFilmAsync(Film, ["eng", "fre"], []);
        await rig.ScanAsync();
        Assert.Equal("would_change", (string)(await rig.FileRowAsync(film))["classification"]!);
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        return new OpenLibrary(page, log, film, LibraryRig.HashOf(film));
    }

    private static async Task SelectFilmAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Select ") }).CheckAsync();
        await Expect(page.GetByTestId("library-bulk")).ToContainTextAsync("1 selected");
    }

    /// <summary>"Clean these files", which sends the page's one request (asking what the clean would remove) and opens the question it answers.</summary>
    private static async Task<ILocator> AskToCleanSelectionAsync(IPage page, RequestLog log)
    {
        var mark = log.Mark();
        await page.GetByRole(AriaRole.Button, new() { Name = "Clean these files", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("library-confirm");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = ServerMs });
        AssertOnlyAskedWhatTheCleanWouldRemove(Sent(log, mark));
        return dialog;
    }

    /// <summary>What the page sent since <paramref name="mark"/>, leaving out the file panel's own preview of the tracks, which is a question and changes nothing.</summary>
    private static IReadOnlyList<SentRequest> Sent(RequestLog log, int mark) =>
        [.. log.Since(mark).Where(request => !request.PathAndQuery.EndsWith("/preview", StringComparison.Ordinal))];

    private static void AssertOnlyAskedWhatTheCleanWouldRemove(IReadOnlyList<SentRequest> sent)
    {
        var asked = Assert.Single(sent);
        Assert.Equal("POST", asked.Method);
        Assert.EndsWith("/library-files/clean", asked.PathAndQuery, StringComparison.Ordinal);
        Assert.False((bool)JsonNode.Parse(asked.Body!)!["confirm_final_removal"]!, "Asking what the clean would remove must not confirm it.");
    }

    /// <summary>The page sent nothing since <paramref name="mark"/>, no clean was queued or finished, and the film is byte for byte what was scanned.</summary>
    private static async Task AssertNothingCleanedAsync(LibraryRig rig, RequestLog log, int mark, OpenLibrary open)
    {
        var row = await rig.FileRowAsync(open.Film);
        Assert.Empty(Sent(log, mark));
        Assert.Equal(0, await rig.CleanJobCountAsync());
        Assert.Empty(await rig.CleansAsync());
        Assert.Equal("would_change", (string)row["classification"]!);
        Assert.Equal(1, (int)row["removed_audio_tracks"]!);
        Assert.Equal(open.Hash, LibraryRig.HashOf(open.Film));
    }


    /// <summary>The library page as a test finds it: the film scanned, and the film's bytes as they were then.</summary>
    private sealed record OpenLibrary(IPage Page, RequestLog Log, string Film, string Hash);
}
