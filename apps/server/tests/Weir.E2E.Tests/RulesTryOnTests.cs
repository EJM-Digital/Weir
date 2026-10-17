using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Rules › Profiles › Check the rules against real films read by a real server: the panel stays closed until asked and closes again, and a
/// film the rules would change is told apart from one they leave alone, with the container notes and the plan behind each.
/// </summary>
public sealed class RulesTryOnTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private const string Changed = "Arrival (2016)/Arrival (2016).mkv";
    private const string Matching = "Heat (1995)/Heat (1995).mkv";

    private static ILocator Workspace(IPage page) => page.GetByTestId("processing-rule-set-workspace");

    private static ILocator Open(IPage page) => Workspace(page).GetByRole(AriaRole.Button, new() { Name = "Try on a file", Exact = true });

    private static ILocator Panel(IPage page) => Workspace(page).GetByRole(AriaRole.Region, new() { Name = "Check the rules" });

    private async Task<(LibraryRig Rig, IPage Page)> OpenProfilesAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            await AddFilmWithFrenchAudioAtAsync(rig, Changed);
            await rig.AddMatchingFilmAsync(Matching, "eng");
            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await Navigation.OpenTabAsync(page, "Rules", "Profiles");
            await Expect(Open(page)).ToBeVisibleAsync(new() { Timeout = ServerMs });
            return (rig, page);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    /// <summary>A film of English and French audio and English subtitles in which the French audio is 640 kbit/s, so the rules have a size to estimate for what they drop.</summary>
    private static async Task AddFilmWithFrenchAudioAtAsync(LibraryRig rig, string relativePath)
    {
        var probe = FakeMedia.Probe(audioLanguages: ["eng", "fre"], subtitleLanguages: ["eng"], durationSeconds: 60);
        probe["streams"]!.AsArray()[2]!["bit_rate"] = "640000";
        var path = Path.Combine(rig.LibraryFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, FakeMedia.Bytes(probe));
    }

    private static async Task PreviewAsync(IPage page, LibraryRig rig, string film)
    {
        await Panel(page).GetByRole(AriaRole.Combobox, new() { Name = "Where is the file?" }).SelectOptionAsync("anywhere");
        await Panel(page).GetByLabel("Folder", new() { Exact = true }).FillAsync(Path.GetDirectoryName(Path.Combine(rig.LibraryFolder, film))!);
        await Panel(page).GetByLabel("File name", new() { Exact = true }).FillAsync(Path.GetFileName(film));
        await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
    }

    [E2EFact]
    public async Task The_panel_stays_closed_until_asked_and_Close_folds_it_away_again()
    {
        var (rig, page) = await OpenProfilesAsync();
        await using var _ = rig;
        await Expect(Workspace(page).GetByRole(AriaRole.Heading, new() { Name = "Check the rules" })).ToBeVisibleAsync();
        await Expect(Panel(page)).ToHaveCountAsync(0);
        await Expect(Workspace(page)).ToContainTextAsync("Nothing is processed or queued.");

        await Open(page).ClickAsync();

        await Expect(Panel(page)).ToBeVisibleAsync();
        await Expect(Open(page)).ToHaveCountAsync(0);
        await Expect(Panel(page)).ToContainTextAsync("Read-only. It re-runs a moment after you stop editing the rules above.");
        await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();

        await Expect(Panel(page)).ToHaveCountAsync(0);
        await Expect(Open(page)).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task A_film_the_rules_would_change_shows_its_plan_the_container_changes_and_the_original_language_note()
    {
        var (rig, page) = await OpenProfilesAsync();
        await using var _ = rig;
        var workspace = Workspace(page);
        await workspace.Locator("details").Filter(new() { HasText = "Also remove" }).Locator("summary").ClickAsync();
        await workspace.GetByRole(AriaRole.Checkbox, new() { Name = "Language tags" }).CheckAsync();
        await workspace.GetByRole(AriaRole.Checkbox, new() { Name = "The file's built-in title" }).CheckAsync();
        await workspace.Locator("details").Filter(new() { HasText = "Original language" }).Locator("summary").ClickAsync();
        await workspace.GetByRole(AriaRole.Checkbox, new() { Name = "Keep the original language" }).CheckAsync();
        await Open(page).ClickAsync();

        await PreviewAsync(page, rig, Changed);

        var panel = Panel(page);
        await Expect(panel).ToContainTextAsync("Weir would rewrite this file", new() { Timeout = ServerMs });
        await Expect(panel).Not.ToContainTextAsync("Already matches these rules");
        await Expect(panel).ToContainTextAsync("Estimated size change: about 4.6 MB smaller (estimate only)");
        var plan = panel.GetByRole(AriaRole.Table, new() { Name = "Per-track plan for the previewed file" });
        await Expect(plan.Locator("tbody tr")).ToHaveCountAsync(4);
        await Expect(plan.Locator("tbody tr").Nth(2)).ToContainTextAsync("Drop");
        await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Container changes" })).ToBeVisibleAsync();
        await Expect(panel.Locator("ul.mm-rules-preview__list").First.Locator("li")).ToHaveTextAsync(["Removed the container title.", "Removed stream language tags."]);
        await Expect(panel).ToContainTextAsync("Original language: Original-language selection did not apply (the metadata lookup is switched off on this server), so the configured language preferences chose the track.");
        var notes = panel.Locator("details.mm-rules-preview__notes");
        await Expect(notes.Locator("summary")).ToContainTextAsync("Full plan notes (4)");
        await Expect(notes).Not.ToHaveAttributeAsync("open", "");
        await notes.Locator("summary").ClickAsync();
        await Expect(notes.Locator("li")).ToHaveCountAsync(4);
    }

    [E2EFact]
    public async Task A_film_that_already_matches_the_rules_says_so_and_lists_no_changes()
    {
        var (rig, page) = await OpenProfilesAsync();
        await using var _ = rig;
        await Open(page).ClickAsync();

        await PreviewAsync(page, rig, Matching);

        var panel = Panel(page);
        await Expect(panel).ToContainTextAsync("Already matches these rules — no changes needed", new() { Timeout = ServerMs });
        await Expect(panel).Not.ToContainTextAsync("Weir would rewrite this file");
        await Expect(panel).Not.ToContainTextAsync("Estimated size change");
        await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Container changes" })).ToHaveCountAsync(0);
        await Expect(panel.GetByRole(AriaRole.Table, new() { Name = "Per-track plan for the previewed file" }).Locator("tbody tr")).ToHaveCountAsync(2);
        await Expect(panel.Locator("details.mm-rules-preview__notes summary")).ToContainTextAsync("Full plan notes (2)");
    }
}
