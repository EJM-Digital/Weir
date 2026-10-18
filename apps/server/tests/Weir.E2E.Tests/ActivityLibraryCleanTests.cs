using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Library cleans in Activity, in a real browser: a clean of a file in the library is listed beside downloads with how it ended (cleaned in
/// place, already matched the rules, failed), each under the chip it belongs to, and choosing one opens its own record and writes it in the
/// address.
/// </summary>
public sealed partial class ActivityLibraryCleanTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";
    private const string Heat = "Heat (1995)/Heat (1995).mkv";
    private const string Zodiac = "Zodiac (2007)/Zodiac (2007).mkv";

    [GeneratedRegex(@"(Arrival|Heat|Zodiac) \(\d{4}\)\.mkv")]
    private static partial Regex CleanedFile();

    [E2EFact]
    public async Task Each_clean_is_listed_with_how_it_ended_under_its_own_chip_and_choosing_one_opens_its_record_and_writes_it_in_the_address()
    {
        await using var rig = await StartRigAsync();
        var page = await NewPageAsync(Wide);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(Rows(page)).ToHaveCountAsync(3, new() { Timeout = ServerMs });
        var cleans = await rig.CleansAsync();
        Assert.Equal(["cleaned", "failed", "skipped"], cleans.Select(clean => (string)clean["outcome"]!).Order());

        await Expect(Row(page, "Arrival (2016).mkv")).ToContainTextAsync("Cleaned in place");
        await Expect(Row(page, "Heat (1995).mkv")).ToContainTextAsync("Already matched the rules");
        await Expect(Row(page, "Zodiac (2007).mkv")).ToContainTextAsync("Clean failed");
        foreach (var (chip, film) in new[] { ("Finished", "Arrival (2016).mkv"), ("Skipped", "Heat (1995).mkv"), ("Failed", "Zodiac (2007).mkv") })
        {
            await Chip(page, chip).ClickAsync();

            await Expect(Rows(page)).ToHaveCountAsync(1, new() { Timeout = ServerMs });
            await Expect(Rows(page)).ToContainTextAsync(film);
        }

        await Chip(page, "All").ClickAsync();
        await Expect(Rows(page)).ToHaveCountAsync(3, new() { Timeout = ServerMs });

        foreach (var film in new[] { "Heat (1995).mkv", "Arrival (2016).mkv" })
        {
            await Row(page, film).GetByRole(AriaRole.Button).ClickAsync();

            var path = film[..film.IndexOf(" (", StringComparison.Ordinal)];
            var clean = cleans.Single(entry => ((string)entry["relative_path"]!).Contains(path, StringComparison.Ordinal));
            await Expect(page).ToHaveURLAsync(new Regex($"[?&]clean={(long)clean["id"]!}(&|$)"), new() { Timeout = ServerMs });
            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]file="));
            await Expect(Row(page, film)).ToHaveAttributeAsync("aria-current", "true");
            await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(film);
        }
    }

    private static ILocator Chip(IPage page, string name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new($"^{Regex.Escape(name)}") });

    private static ILocator Row(IPage page, string film) => Rows(page).Filter(new() { HasText = film });

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = CleanedFile() });

    /// <summary>A workflow whose rules keep only English audio, with three films that need cleaning when they are scanned, then cleaned: one is cleaned, one has been put right by hand meanwhile and one cannot be read.</summary>
    private static async Task<LibraryRig> StartRigAsync()
    {
        var rig = await LibraryRig.StartAsync();
        try
        {
            await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
            await rig.AddFilmAsync(Heat, ["eng", "fre"], []);
            await rig.AddFilmAsync(Zodiac, ["eng", "fre"], []);
            await rig.UseRulesKeepingAudioAsync("English only", "eng");
            await rig.ScanAsync();
            // By the time the cleans come to them one film has been put right by hand, and another has been overwritten with something Weir cannot read.
            await rig.AddMatchingFilmAsync(Heat, "eng");
            await File.WriteAllBytesAsync(Path.Combine(rig.LibraryFolder, Zodiac), new byte[2048]);
            await rig.CleanAsync(Path.Combine(rig.LibraryFolder, Arrival), Path.Combine(rig.LibraryFolder, Heat), Path.Combine(rig.LibraryFolder, Zodiac));
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }
}
