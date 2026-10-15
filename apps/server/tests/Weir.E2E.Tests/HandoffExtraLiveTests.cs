using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A release folder handed over with its film and an extra under the workflow's minimum size: the extra is left alone, which
/// needs nobody, and the Dashboard shows how the film ended, all pushed to the open page with no reload.
/// </summary>
public sealed class HandoffExtraLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    // A hang guard, not a pace: the film waits behind the extra on the one worker, and every tool call is a process to start, which a
    // busy machine stretches. Each wait ends the moment the page shows what it waits for.
    private const float PassMs = 120_000;

    private const string Release = "h-nosferatu";
    private const string Film = "Nosferatu.1922.mkv";
    private const string Extra = "Gallery.mkv";

    private async Task<IPage> OpenDashboardAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        return page;
    }

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    /// <summary>Completes once the page has been given the skipped extra among the files that may need the person.</summary>
    private static Task WatchForSkippedExtra(IPage page)
    {
        var seen = new TaskCompletionSource();
        page.Response += async (_, response) =>
        {
            if (!response.Url.Contains("/api/v1/processing/files", StringComparison.Ordinal) ||
                !response.Url.Contains("skipped", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if ((await response.TextAsync()).Contains(Extra, StringComparison.Ordinal))
                {
                    seen.TrySetResult();
                }
            }
            catch (PlaywrightException)
            {
                // The page moved on before the body could be read; the next answer says the same.
            }
        };
        return seen.Task;
    }

    [E2EFact]
    public async Task An_extra_under_the_minimum_size_is_left_alone_and_nothing_needs_the_person()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1);
        var page = await OpenDashboardAsync(rig);
        var needs = page.GetByRole(AriaRole.Region, new() { Name = "Needs you" });
        var shelf = page.GetByTestId("just-finished-shelf");
        var told = WatchForSkippedExtra(page);
        await Expect(needs).ToContainTextAsync("All clear");

        await rig.HandOffReleaseAsync(Release, Film, Extra);
        rig.ReleasePass(Film);

        await Expect(shelf.GetByRole(AriaRole.Button, new() { Name = "Nosferatu (1922)" })).ToBeVisibleAsync(new() { Timeout = PassMs });
        await told.WaitAsync(TimeSpan.FromMilliseconds(PassMs));
        await rig.WaitForReportAsync();
        await rig.ReportImportedAsync(Release);

        // The film is the one file that finished: the extra, left alone, is no tile and no "cleaned" line.
        await Expect(shelf.GetByRole(AriaRole.Button)).ToHaveCountAsync(1);
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Activity" })).Not.ToContainTextAsync("Gallery");
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Today" })).ToContainTextAsync("1 file finished in the last 2 hours", new() { Timeout = PushMs });
        await Expect(needs).ToContainTextAsync("All clear", new() { Timeout = PushMs });
        await Expect(page.GetByTestId("nav-activity-needs-you")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("need a look") })).ToHaveCountAsync(0);
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task A_file_that_failed_and_was_tried_again_shows_how_it_ended_and_not_the_failure()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        var page = await OpenDashboardAsync(rig);
        var shelf = page.GetByTestId("just-finished-shelf");
        var film = shelf.GetByRole(AriaRole.Button, new() { Name = "Nosferatu (1922)" });

        await rig.HandOffReleaseAsync(Release, Film, Extra, failFirstPass: true);

        await Expect(film).ToHaveAttributeAsync("aria-label", new Regex("Could not be finished"), new() { Timeout = PassMs });
        await page.GetByRole(AriaRole.Region, new() { Name = "Needs you" }).GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();

        await Expect(film).Not.ToHaveAttributeAsync("aria-label", new Regex("Could not be finished"), new() { Timeout = PassMs });
        await Expect(film).ToHaveCountAsync(1);
        await AssertNotReloadedAsync(page);
    }
}
