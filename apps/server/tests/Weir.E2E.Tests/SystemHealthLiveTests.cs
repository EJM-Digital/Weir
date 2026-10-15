using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Dashboard › System follows Weir's own look at a workflow's folders: it turns red when they go and green when they are back, with no reload.</summary>
public sealed class SystemHealthLiveTests(E2EServer server) : E2ETestBase(server)
{
    // The server looks at the folders every 15 s while a browser watches, and these tests run it ten times as often. The page reads
    // the answer when it is told; this is the most it is given, and only a hung server reaches it.
    private const float FolderCheckMs = 20_000;

    private const int QuickTasks = 10;

    // The look is run only twice as often in the test of the checked time, so the age the row shows climbs well past "just now"
    // (under 3 s) before the next look sets it back.
    private const int SteadyTasks = 2;

    // The most the checked time is given to climb and then be set back by the server's next look.
    private const float LookAgainMs = 30_000;

    [E2EFact]
    public async Task A_workflow_whose_folder_goes_missing_turns_red_and_green_again_when_it_is_back_without_a_reload()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await using var own = await E2EServer.StartWithQuickTasksAsync(QuickTasks);
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, baseUrl, "E2E checked films", watched, output);
        try
        {
            await page.GotoAsync($"{baseUrl}/?view=system");
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            await page.GetByRole(AriaRole.Group, new() { Name = "Health areas" }).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Workflows") }).ClickAsync();
            var row = page.GetByTestId("system-check").Filter(new() { HasText = "E2E checked films" });
            await Expect(row).ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });

            // Unreachable, the way a share that drops off the network is: the folder is simply not there.
            Directory.Delete(watched);
            await Expect(row).Not.ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            await Expect(row).ToContainTextAsync("needs a fix");

            Directory.CreateDirectory(watched);
            await Expect(row).ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            await Expect(row).Not.ToContainTextAsync("needs a fix");

            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            // A job of the workflow cannot finish while its folder is gone, and Weir will not remove a workflow with a job left.
            Directory.CreateDirectory(watched);
            await WeirWorkflowApi.DeleteAsync(page.Context, baseUrl, id);
        }
    }

    // The time of the next look the server tells a stream of the test's own about, later than `after` when one is given.
    private static async Task<DateTimeOffset> NextFolderLookAsync(SseReader stream, DateTimeOffset? after)
    {
        while (true)
        {
            var frame = await stream.NextEventNamedAsync("system.checks", TimeSpan.FromMilliseconds(LookAgainMs));
            if (frame["folders_checked_at"]?.GetValue<string>() is { } text
                && DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture) is var at
                && (after is null || at > after))
            {
                return at;
            }
        }
    }

    [E2EFact]
    public async Task A_workflows_checked_time_is_the_servers_last_look_not_the_pages_last_read_and_a_drive_says_live()
    {
        using var folders = new TemporaryFolder();
        await using var own = await E2EServer.StartWithQuickTasksAsync(SteadyTasks);
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        var id = await WeirWorkflowApi.CreateAsync(
            page.Context,
            baseUrl,
            "E2E looked-at films",
            Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName,
            Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName);
        try
        {
            await page.GotoAsync($"{baseUrl}/?view=system");

            // The card lists only the rows that fit, problems first, and a hidden row has no text on screen. Among every check this
            // row can fall below a missing tool and be left out, as it is on a machine without FFmpeg; under Workflows it is not.
            await page.GetByRole(AriaRole.Group, new() { Name = "Health areas" }).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Workflows") }).ClickAsync();
            var row = page.GetByTestId("system-check").Filter(new() { HasText = "E2E looked-at films" });
            await Expect(row).ToHaveAttributeAsync("data-status", "done", new() { Timeout = FolderCheckMs });
            await Expect(row).ToBeVisibleAsync();
            await Expect(row).ToContainTextAsync("checked");

            // The server's own look times, read from a stream of the test's own and not from the page: the page reads the folders once
            // and nothing after, so a row that shows the time of its own read grows older than the next look, while a row that shows the
            // server's look is never older than the interval between looks.
            using var watcher = new WeirClient(own.BaseUrl);
            await watcher.LoginAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            using var stream = await watcher.OpenStreamAsync("/api/v1/activity/stream");
            var firstLook = await NextFolderLookAsync(stream, after: null);
            var nextLook = await NextFolderLookAsync(stream, after: firstLook);

            await page.WaitForFunctionAsync(
                """
                ([title, lookAt]) => {
                  const row = [...document.querySelectorAll('[data-testid="system-check"]')].find((item) => item.textContent.includes(title));
                  const shown = /checked (just now|(\d+)s ago)/.exec(row?.innerText ?? '');
                  if (!shown) return false;
                  const sinceLook = (Date.now() - lookAt) / 1000;
                  return shown[1] === 'just now' ? sinceLook < 4.5 : Math.abs(Number(shown[2]) - sinceLook) <= 2;
                }
                """,
                new object[] { "E2E looked-at films", nextLook.ToUnixTimeMilliseconds() },
                new() { Timeout = LookAgainMs });

            await page.GetByRole(AriaRole.Group, new() { Name = "Health areas" }).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Storage") }).ClickAsync();
            await Expect(page.GetByTestId("system-check").First).ToContainTextAsync("live");
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, baseUrl, id);
        }
    }
}
