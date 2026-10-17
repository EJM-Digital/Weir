using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Dashboard's Pipeline follows a real pass: a card for the file at its station with the progress the server pushes, the file's
/// story opened from the card, and the card leaving for Just finished with an announcement for a screen reader. Nothing is
/// intercepted: Weir really processes the file against a fake ffmpeg that holds the pass until the test releases it.
/// </summary>
public sealed partial class PipelineBoardLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float FinishMs = 30_000;
    private const string FileName = "Harbour Lights 2024.mkv";
    private const string Title = "Harbour Lights (2024)";

    // The announcement lives for the few seconds a card takes to leave, so a page records every text it holds.
    private const string RecordAnnouncements = """
        (() => {
          window.__announced = [];
          const watch = () => {
            const region = document.querySelector('[data-testid="pipeline-announcement"]');
            if (!region) return false;
            new MutationObserver(() => window.__announced.push(region.textContent)).observe(region, { childList: true, characterData: true, subtree: true });
            return true;
          };
          const waiting = new MutationObserver(() => { if (watch()) waiting.disconnect(); });
          waiting.observe(document, { childList: true, subtree: true });
        })();
        """;

    [GeneratedRegex(@"/api/v1/processing/files/(\d+)/log$")]
    private static partial Regex FileLogPath();

    private static ILocator Card(IPage page, string title) =>
        page.GetByTestId("pipeline-board").GetByRole(AriaRole.Button, new() { NameRegex = new Regex($"^{Regex.Escape(title)}:") });

    private async Task<IPage> OpenDashboardAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        await page.AddInitScriptAsync(RecordAnnouncements);
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        return page;
    }

    [E2EFact]
    public async Task A_file_being_written_shows_its_station_and_the_progress_the_server_pushes_and_its_card_opens_the_story_with_Right_now()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenDashboardAsync(rig);
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Nothing in progress. New downloads appear here.");

            await rig.HandOffAsync("h-board", FileName, progressSeconds: 30);

            var card = Card(page, Title);
            await Expect(card).ToHaveAccessibleNameAsync(new Regex(@"^Harbour Lights \(2024\): 50% · "), new() { Timeout = FinishMs });
            await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Processing: 1", Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Queued: 0", Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByTestId("live-working").GetByRole(AriaRole.Button)).ToContainTextAsync("50%");
            var listed = await rig.ListedFileAsync(FileName);
            Assert.NotNull(listed);
            var id = (long)listed["id"]!;

            // A card is a button: it takes focus and Enter opens the file's story, which is read from the server then.
            await card.FocusAsync();
            var read = await page.RunAndWaitForResponseAsync(
                () => page.Keyboard.PressAsync("Enter"),
                response => response.Request.Method == "GET" && FileLogPath().IsMatch(response.Url));
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), FileLogPath().Match(read.Url).Groups[1].Value);
            Assert.Equal(200, read.Status);
            var story = page.GetByRole(AriaRole.Dialog, new() { Name = Title });
            await Expect(story.GetByRole(AriaRole.Heading, new() { Name = Title, Exact = true })).ToBeVisibleAsync();
            var now = story.GetByRole(AriaRole.Region, new() { Name = "Right now" });
            await Expect(now).ToContainTextAsync("Processing");
            await Expect(now).ToContainTextAsync("50% · ");
            await Expect(now.GetByRole(AriaRole.Progressbar)).ToBeVisibleAsync();
            await Expect(now).ToContainTextAsync(FileName);

            await story.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await Expect(story).ToHaveCountAsync(0);
            await card.ClickAsync();
            await Expect(story).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(story).ToHaveCountAsync(0);
        });
    }

    [E2EFact]
    public async Task A_finished_file_leaves_the_board_announced_as_delivered_and_its_story_has_no_Right_now_section()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenDashboardAsync(rig);
            await rig.HandOffAsync("h-leave", FileName);
            var card = Card(page, Title);
            await Expect(card).ToBeVisibleAsync(new() { Timeout = FinishMs });

            rig.ReleasePass(FileName);

            await page.WaitForFunctionAsync(
                "(words) => window.__announced.some((text) => text.includes(words))",
                $"{Title} was delivered. It moves to Just finished.",
                new() { Timeout = FinishMs });
            await Expect(card).ToHaveCountAsync(0, new() { Timeout = FinishMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Nothing in progress. New downloads appear here.");
            var finished = page.GetByRole(AriaRole.Region, new() { Name = "Just finished" }).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("harbour lights", RegexOptions.IgnoreCase) });
            await Expect(finished).ToBeVisibleAsync(new() { Timeout = FinishMs });

            await finished.ClickAsync();

            var story = page.GetByRole(AriaRole.Dialog, new() { Name = Title });
            await Expect(story).ToBeVisibleAsync();
            await Expect(story.GetByRole(AriaRole.Region, new() { Name = "Right now" })).ToHaveCountAsync(0);
            await Expect(story).Not.ToContainTextAsync("shows here once it has finished");
        });
    }

    [E2EFact]
    public async Task More_than_three_files_at_a_station_show_three_rows_hide_the_rest_from_assistive_technology_and_end_in_a_link_to_the_list()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenDashboardAsync(rig);
            for (var number = 1; number <= 5; number++)
            {
                await rig.HandOffAsync($"h-many-{number}", $"Film Number {number} 2024.mkv");
            }

            // One worker has the first; the other four wait, in line, in the Queued station.
            await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Queued: 4", Exact = true })).ToBeVisibleAsync(new() { Timeout = FinishMs });
            var more = page.GetByTestId("pipeline-board").GetByRole(AriaRole.Link, new() { Name = "and 1 more →", Exact = true });
            await Expect(more).ToBeVisibleAsync();
            var hidden = page.Locator(".mm-pipe__card--hidden");
            await Expect(hidden).ToHaveCountAsync(1);
            await Expect(hidden).ToHaveAttributeAsync("aria-hidden", "true");
            await Expect(hidden).ToHaveAttributeAsync("tabindex", "-1");
            await Expect(page.GetByTestId("pipeline-board").Locator(".mm-pipe__card:not(.mm-pipe__card--hidden)")).ToHaveCountAsync(4);
            await Expect(more).ToHaveAttributeAsync("title", new Regex("^Queued: "));

            await more.ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex(@"/activity\?show=working$"));
        });
    }
}
