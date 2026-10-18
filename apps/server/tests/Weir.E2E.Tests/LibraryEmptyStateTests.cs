using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// What the Library page says when the Files table has nothing to list, read from a real server: a workflow nobody has scanned, a status
/// with no files in it (one sentence for each status), and a search or a status and a search that match nothing.
/// </summary>
public sealed class LibraryEmptyStateTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";

    [E2EFact]
    public async Task A_workflow_that_has_not_been_scanned_says_so_and_points_at_Check_again_and_Library_setup()
    {
        await using var rig = await LibraryRig.StartAsync();
        await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
        var page = await OpenLibraryAsync(rig);

        await Expect(page.GetByTestId("library-empty")).ToHaveTextAsync(
            "This workflow has not been scanned yet, or its folders hold nothing Weir reads. Check again, or change its folders in Library setup.",
            new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true })).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Library setup" })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task A_status_with_no_files_says_what_that_means_in_its_own_words_and_the_chip_can_still_be_chosen()
    {
        await using var rig = await StartRigAsync(("Arrival", Arrival, ["eng", "fre"]), ("Heat", Heat, ["eng"]));
        var page = await OpenLibraryAsync(rig);
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        var empty = page.GetByTestId("library-empty");

        await ChooseAsync(page, "Cleaning");
        await Expect(empty).ToHaveTextAsync("No files are being cleaned right now.", new() { Timeout = ServerMs });

        await ChooseAsync(page, "Can't clean");
        await Expect(empty).ToHaveTextAsync("No files are held back right now.", new() { Timeout = ServerMs });

        await ChooseAsync(page, "Left alone");
        await Expect(empty).ToHaveTextAsync("You have not set any file aside.", new() { Timeout = ServerMs });

        await ChooseAsync(page, "Needs cleaning");
        await Expect(empty).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1);
    }

    [E2EFact]
    public async Task A_library_where_every_film_matches_says_no_file_needs_cleaning_and_one_where_none_do_says_none_match()
    {
        await using var matching = await StartRigAsync(("Heat", Heat, ["eng"]));
        var page = await OpenLibraryAsync(matching);
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });

        await ChooseAsync(page, "Needs cleaning");

        await Expect(page.GetByTestId("library-empty")).ToHaveTextAsync("No files need cleaning right now.", new() { Timeout = ServerMs });

        await using var needing = await StartRigAsync(("Arrival", Arrival, ["eng", "fre"]));
        var other = await OpenLibraryAsync(needing);
        await Expect(other.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });

        await ChooseAsync(other, "Matches rules");

        await Expect(other.GetByTestId("library-empty")).ToHaveTextAsync("No files match your rules right now.", new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task A_search_that_matches_nothing_says_so_and_clearing_it_lists_the_files_again()
    {
        await using var rig = await StartRigAsync(("Arrival", Arrival, ["eng", "fre"]), ("Heat", Heat, ["eng"]));
        var page = await OpenLibraryAsync(rig);
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this workflow" });
        var empty = page.GetByTestId("library-empty");
        const string Nothing = "Nothing here matches what you asked for. Clear the filters to see the whole library.";

        await search.FillAsync("zzz");

        await Expect(empty).ToHaveTextAsync(Nothing, new() { Timeout = ServerMs });

        await search.FillAsync(string.Empty);

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });
    }

    [E2EFact]
    public async Task A_status_in_the_address_and_a_search_that_matches_none_of_its_files_say_so_and_clearing_the_search_lists_that_status_again()
    {
        await using var rig = await StartRigAsync(("Arrival", Arrival, ["eng", "fre"]), ("Heat", Heat, ["eng"]));
        var page = await OpenLibraryAsync(rig, "&show=needs_cleaning");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this workflow" });

        await search.FillAsync("heat");

        await Expect(page.GetByTestId("library-empty")).ToHaveTextAsync(
            "Nothing here matches what you asked for. Clear the filters to see the whole library.",
            new() { Timeout = ServerMs });

        await search.FillAsync(string.Empty);

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-row")).ToContainTextAsync("Arrival (2016).mkv");
    }

    private static async Task ChooseAsync(IPage page, string chip)
    {
        var button = page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new($"^{System.Text.RegularExpressions.Regex.Escape(chip)}") });
        await button.ClickAsync();
        await Expect(button).ToHaveAttributeAsync("aria-pressed", "true");
    }

    /// <summary>A workflow whose rules keep only English audio, with these films (name, path, audio languages), scanned.</summary>
    private static async Task<LibraryRig> StartRigAsync(params (string Name, string Path, string[] Audio)[] films)
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            foreach (var film in films)
            {
                if (film.Audio.Length > 1)
                {
                    await rig.AddFilmAsync(film.Path, film.Audio, []);
                }
                else
                {
                    await rig.AddMatchingFilmAsync(film.Path, film.Audio[0]);
                }
            }

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

    private async Task<IPage> OpenLibraryAsync(LibraryRig rig, string extraQuery = "")
    {
        var page = await NewPageAsync(Desktop);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}{extraQuery}");
        await Expect(page.GetByTestId("library-controls")).ToBeVisibleAsync(new() { Timeout = ServerMs });
        return page;
    }
}
