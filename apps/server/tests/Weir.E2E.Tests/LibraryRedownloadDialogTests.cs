using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// "Download again" has the media manager delete a file before it searches for a new copy, so the file's panel asks first. Leaving
/// that question any way but "Yes" (Not now, Escape) must send nothing, and clicking beside it must leave it open. The page's
/// requests, Radarr's own record of what it was asked, Weir's answer and the film on disk are all read back each time.
/// </summary>
public sealed class LibraryRedownloadDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const long MovieId = 7;
    private const string Film = "Arrival (2016)/Arrival (2016).mkv";

    [E2EFact]
    public async Task Not_now_sends_nothing_and_Radarr_is_not_asked_to_delete_or_search()
    {
        await using var rig = await StartRigWithFrenchRemovedAsync();
        var open = await OpenFilePanelAsync(rig);
        var dialog = await AskToDownloadAgainAsync(open.Page);
        var (mark, radarrMark) = (open.Log.Mark(), rig.RadarrMark());

        await dialog.GetByTestId("library-redownload-confirm-cancel").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingAskedAsync(rig, open, mark, radarrMark);
    }

    [E2EFact]
    public async Task Escape_sends_nothing_and_Radarr_is_not_asked_to_delete_or_search()
    {
        await using var rig = await StartRigWithFrenchRemovedAsync();
        var open = await OpenFilePanelAsync(rig);
        var dialog = await AskToDownloadAgainAsync(open.Page);
        var (mark, radarrMark) = (open.Log.Mark(), rig.RadarrMark());

        await open.Page.Keyboard.PressAsync("Escape");

        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingAskedAsync(rig, open, mark, radarrMark);
    }

    [E2EFact]
    public async Task A_click_beside_the_question_leaves_it_open_and_sends_nothing()
    {
        await using var rig = await StartRigWithFrenchRemovedAsync();
        var open = await OpenFilePanelAsync(rig);
        var dialog = await AskToDownloadAgainAsync(open.Page);
        var (mark, radarrMark) = (open.Log.Mark(), rig.RadarrMark());

        await Backdrop.ClickAsync(open.Page, dialog);

        await Expect(dialog).ToBeVisibleAsync();
        await AssertNothingAskedAsync(rig, open, mark, radarrMark);
        await dialog.GetByTestId("library-redownload-confirm-cancel").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await AssertNothingAskedAsync(rig, open, mark, radarrMark);
    }

    [E2EFact]
    public async Task Yes_has_Radarr_delete_the_file_and_search_once_and_the_panel_says_so()
    {
        await using var rig = await StartRigWithFrenchRemovedAsync();
        rig.RadarrDeletesFiles();
        var open = await OpenFilePanelAsync(rig);
        var dialog = await AskToDownloadAgainAsync(open.Page);
        var (mark, radarrMark) = (open.Log.Mark(), rig.RadarrMark());

        await dialog.GetByTestId("library-redownload-confirm-confirm").ClickAsync();

        var panel = open.Page.GetByTestId("library-redownload");
        await Expect(panel.GetByRole(AriaRole.Status)).ToContainTextAsync("deleted the existing file and is searching for a replacement", new() { Timeout = ServerMs });
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Download again" })).ToHaveCountAsync(0);
        var sent = Assert.Single(open.Log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-redownloads", sent.PathAndQuery, StringComparison.Ordinal);
        var body = JsonNode.Parse(sent.Body!)!;
        Assert.Equal(open.Film, (string)body["path"]!);
        Assert.True((bool)body["confirm_destructive"]!);
        Assert.Equal(
            [$"GET /api/v3/movie/{MovieId}", $"DELETE /api/v3/moviefile/{MovieId * 10}", "POST /api/v3/command"],
            rig.RadarrAskedAboutAFileSince(radarrMark));
        Assert.True(await rig.LoggedAsync("library.redownload_requested"));
    }

    /// <summary>
    /// A Radarr-managed film that was cleaned of its French audio, and a workflow whose rules now keep French: the one case where
    /// the server offers "Download again". It is reached the way a person reaches it, through a scan, a clean and a rules change.
    /// </summary>
    private static async Task<LibraryRig> StartRigWithFrenchRemovedAsync()
    {
        var rig = await LibraryRig.StartAsync(withRadarr: true);
        try
        {
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            var film = await rig.AddFilmAsync(Film, ["eng", "fre"], [], MovieId, "Arrival");
            await rig.ScanAsync();
            await rig.CleanAsync(film);
            await rig.UseRulesKeepingAudioAsync("English and French", "eng", "fre");
            await rig.ScanAsync();
            var title = (await rig.RedownloadsAsync())["titles"]!.AsArray().Single()!;
            Assert.True((bool)title["can_redownload"]!, title.ToJsonString());
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<OpenFile> OpenFilePanelAsync(LibraryRig rig)
    {
        var film = (string)(await rig.FileRowAsync(Path.Combine(rig.LibraryFolder, Film)))["path"]!;
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}&path={Uri.EscapeDataString(film)}");
        await Expect(page.GetByTestId("library-redownload")).ToBeVisibleAsync(new() { Timeout = ServerMs });
        return new OpenFile(page, log, film, LibraryRig.HashOf(Path.Combine(rig.LibraryFolder, Film)));
    }

    private static async Task<ILocator> AskToDownloadAgainAsync(IPage page)
    {
        await page.GetByTestId("library-redownload").GetByRole(AriaRole.Button, new() { Name = "Download again", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("library-redownload-confirm");
        await Expect(dialog).ToBeVisibleAsync();
        return dialog;
    }

    /// <summary>
    /// The page sent nothing since <paramref name="mark"/>, Radarr was asked nothing about the file since <paramref name="radarrMark"/>,
    /// and Weir still offers the same "Download again", has logged none and holds the film byte for byte as it was.
    /// </summary>
    private static async Task AssertNothingAskedAsync(LibraryRig rig, OpenFile open, int mark, int radarrMark)
    {
        var title = (await rig.RedownloadsAsync())["titles"]!.AsArray().Single()!;
        open.Log.AssertNothingSentSince(mark);
        Assert.Empty(rig.RadarrAskedAboutAFileSince(radarrMark));
        Assert.True((bool)title["can_redownload"]!);
        Assert.Null(title["waiting_note"]);
        Assert.False(await rig.LoggedAsync("library.redownload_requested"));
        Assert.Equal(open.Hash, LibraryRig.HashOf(Path.Combine(rig.LibraryFolder, Film)));
    }

    /// <summary>The Library page open on the film's panel, with the film's bytes as they were.</summary>
    private sealed record OpenFile(IPage Page, RequestLog Log, string Film, string Hash);
}
