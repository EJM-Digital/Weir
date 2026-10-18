using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Which buttons Activity offers for a file, state by state, in a real browser against a Weir that holds a file in each state: a button shows
/// only where it can do something, and a file Weir is working on has none.
/// </summary>
public sealed partial class ActivityFileButtonsTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    private const string Waiting = "Waiting Film 2021.mkv";
    private const string ExtraOfWaiting = "Extra D.mkv";

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task Each_state_offers_only_the_buttons_that_can_do_something_for_it()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            await rig.HandOffReleaseAsync("h-waiting", Waiting, ExtraOfWaiting);
            await Poll.UntilAsync(async () => (string?)(await rig.ListedFileAsync(Waiting))?["status"] == "unprocessed", "the film to wait its turn", TimeSpan.FromMinutes(1));
            var page = await NewPageAsync(Wide);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await page.GotoAsync($"{rig.BaseUrl}/activity");
            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count + 2, new() { Timeout = PushMs });

            // What each file offers, in the order the page lays the buttons out.
            var offered = new (string File, string Does, string[] Buttons)[]
            {
                (MixedActivity.Working, "is being written", []),
                (Waiting, "waits its turn", ["Process now", "Move to top", "Pass through unchanged", "Remove from list"]),
                (MixedActivity.Held, "is on hold", ["Choose tracks", "Check again", "Why is this held?", "Pass through unchanged", "Remove from list"]),
                (MixedActivity.Failed, "failed", ["Try again", "Pass through unchanged", "Remove from list"]),
                (MixedActivity.Finished, "is finished", ["Process again", "Remove from list"]),
                (MixedActivity.ExtraOfFinished, "was left alone for being under the minimum size", ["Check again", "Pass through unchanged", "Remove from list"]),
            };

            foreach (var (file, does, buttons) in offered)
            {
                await Rows(page).Filter(new() { HasText = file }).GetByRole(AriaRole.Button).ClickAsync();
                await Expect(Rows(page).Filter(new() { HasText = file })).ToHaveAttributeAsync("aria-current", "true");
                await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(file);

                var shown = await page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button).AllInnerTextsAsync();

                Assert.True(buttons.SequenceEqual(shown.Select(text => text.Trim())), $"{file} {does}: expected [{string.Join(", ", buttons)}] but the page offers [{string.Join(", ", shown)}].");
            }
        });
    }

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });
}
