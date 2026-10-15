using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs follows the job queue: a job queued through the API shows up in the open log, with no reload. A row opens and
/// closes from the keyboard alone.
/// </summary>
public sealed class SystemLogsLiveTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task A_job_queued_through_the_api_appears_in_the_open_log_without_a_reload()
    {
        const string marker = "live-marker-clip";
        var folders = Directory.CreateTempSubdirectory("weir_e2e_live_logs_");
        try
        {
            var watched = Directory.CreateDirectory(Path.Join(folders.FullName, "watched")).FullName;
            var output = Directory.CreateDirectory(Path.Join(folders.FullName, "output")).FullName;
            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            await WeirApi.SetMovieFoldersAsync(page.Context, BaseUrl, watched, output);
            await Navigation.OpenLogsAsync(page, "Jobs");
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            var row = page.GetByText($"Finished for {marker}.mkv", new() { Exact = true });
            await Expect(page.GetByText(marker).First).ToHaveCountAsync(0);

            await WeirApi.EnqueueFileAsync(page.Context, BaseUrl, $"{marker}.mkv");

            await Expect(row).ToBeVisibleAsync(new() { Timeout = 10_000 });
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            folders.Delete(recursive: true);
        }
    }

    [E2EFact]
    public async Task A_row_is_reached_with_tab_and_opens_and_closes_with_enter_and_space()
    {
        const int MostTabStopsBeforeTheFirstRow = 80;
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenLogsAsync(page);
        var firstHead = page.GetByTestId("log-row").First.Locator("button.mm-log-row__head");
        await Expect(firstHead).ToBeVisibleAsync();

        // The row that has the focus is the one the keys act on, even if the log adds a newer row above it meanwhile.
        var focusedHead = page.Locator("button.mm-log-row__head:focus");
        for (var stop = 0; stop < MostTabStopsBeforeTheFirstRow && await focusedHead.CountAsync() == 0; stop++)
        {
            await page.Keyboard.PressAsync("Tab");
        }

        await Expect(focusedHead).ToHaveCountAsync(1);
        await Expect(focusedHead).ToHaveAttributeAsync("aria-expanded", "false");

        await page.Keyboard.PressAsync("Enter");
        await Expect(focusedHead).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(page.Locator("#" + await focusedHead.GetAttributeAsync("aria-controls"))).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Space");
        await Expect(focusedHead).ToHaveAttributeAsync("aria-expanded", "false");

        await page.Keyboard.PressAsync("Space");
        await Expect(focusedHead).ToHaveAttributeAsync("aria-expanded", "true");
        await page.Keyboard.PressAsync("Enter");
        await Expect(focusedHead).ToHaveAttributeAsync("aria-expanded", "false");
    }
}
