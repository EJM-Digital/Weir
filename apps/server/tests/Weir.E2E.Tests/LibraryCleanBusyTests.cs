using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The question in front of a clean while the server is taking the answer, in a real browser against a server whose database is held: the
/// question says it is starting the clean and cannot be dismissed by Escape, Not now or a click beside it, and when the server cannot take
/// the clean the question stays open, says so, and the clean can be confirmed again once the server can.
/// </summary>
public sealed class LibraryCleanBusyTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const float CleanMs = 180_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";

    [E2EFact]
    public async Task While_the_server_takes_the_clean_Escape_Not_now_and_a_click_beside_the_question_do_nothing_and_the_clean_starts_once_the_server_is_free()
    {
        await using var rig = await StartRigAsync(writesGiveUpQuickly: false);
        var (page, log) = await OpenLibraryAsync(rig);
        var dialog = await AskToCleanAsync(page);
        var yes = dialog.GetByTestId("library-confirm-confirm");
        var notNow = dialog.GetByTestId("library-confirm-cancel");
        var mark = log.Mark();

        using (rig.HoldWrites())
        {
            await yes.ClickAsync();

            await Expect(yes).ToHaveTextAsync("Starting the clean…");
            await Expect(yes).ToBeDisabledAsync();
            await Expect(notNow).ToBeDisabledAsync();
            await page.Keyboard.PressAsync("Escape");
            await Dialogs.ClickOutsideAsync(dialog);
            await Expect(dialog).ToBeVisibleAsync();
            Assert.Equal(0, await rig.CleanJobCountAsync());
        }

        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-outcome")).ToBeVisibleAsync(new() { Timeout = CleanMs });
        var sent = Assert.Single(log.Since(mark));
        Assert.True((bool)JsonNode.Parse(sent.Body!)!["confirm_final_removal"]!);
        await Poll.UntilAsync(async () => (await rig.CleansAsync()).Count == 1, "the clean to finish", TimeSpan.FromMilliseconds(CleanMs));
    }

    [E2EFact]
    public async Task When_the_server_cannot_take_the_clean_the_question_stays_open_with_an_alert_and_Yes_works_again_once_it_can()
    {
        await using var rig = await StartRigAsync(writesGiveUpQuickly: true);
        var (page, _) = await OpenLibraryAsync(rig);
        var dialog = await AskToCleanAsync(page);
        var film = Path.Combine(rig.LibraryFolder, Arrival);
        var before = LibraryRig.HashOf(film);

        using (rig.HoldWrites())
        {
            var refused = await page.RunAndWaitForResponseAsync(
                () => dialog.GetByTestId("library-confirm-confirm").ClickAsync(),
                response => response.Url.EndsWith("/library-files/clean", StringComparison.Ordinal),
                new() { Timeout = ServerMs });

            Assert.Equal(503, refused.Status);
            await Expect(dialog.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = ServerMs });
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(dialog.GetByTestId("library-confirm-confirm")).ToBeEnabledAsync();
            Assert.Equal(0, await rig.CleanJobCountAsync());
            Assert.Equal(before, LibraryRig.HashOf(film));
        }

        await dialog.GetByTestId("library-confirm-confirm").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-outcome")).ToBeVisibleAsync(new() { Timeout = CleanMs });
        await Poll.UntilAsync(async () => (await rig.CleansAsync()).Count == 1, "the clean to finish", TimeSpan.FromMilliseconds(CleanMs));
        Assert.NotEqual(before, LibraryRig.HashOf(film));
    }

    private static async Task<ILocator> AskToCleanAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Select ") }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Clean these files", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("library-confirm");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = ServerMs });
        return dialog;
    }

    /// <summary>A workflow whose rules keep only English audio, a film with French too, which needs cleaning, and that film scanned.</summary>
    private static async Task<LibraryRig> StartRigAsync(bool writesGiveUpQuickly)
    {
        var rig = await LibraryRig.StartAsync(writesGiveUpQuickly: writesGiveUpQuickly);
        try
        {
            await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
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

    private async Task<(IPage Page, RequestLog Log)> OpenLibraryAsync(LibraryRig rig)
    {
        var page = await NewPageAsync(Desktop);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        return (page, log);
    }
}
