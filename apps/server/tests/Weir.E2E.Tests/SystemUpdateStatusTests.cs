using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › About › Updates on an install that is not the Windows one: what Weir says about the newest release, read from GitHub's release feed as it
/// recorded it (<c>fixtures/recorded/github-releases</c>), the Check again button, and how a Docker install takes the update. Each test has a server of its own,
/// run as the version it names.
/// </summary>
public sealed class SystemUpdateStatusTests(E2EServer server) : E2ETestBase(server)
{
    private const string Older = "1.0.0-rc.5";
    private const string UpdateStatus = "/api/v1/suite/update-status";
    private const string DockerCommand = "docker compose pull && docker compose up -d";

    private static ILocator Updates(IPage page) => page.GetByTestId("suite-settings-upgrade-tab");

    private static ILocator Status(IPage page) => page.GetByTestId("suite-settings-release-status");

    private static ILocator CheckAgain(IPage page) => Updates(page).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Check again|Checking)") });

    private async Task<SystemSession> OpenAsync(FakeReleaseFeed feed, string version, string runtime, IPage? page = null) =>
        await SystemSession.OpenAsync(page ?? await NewPageAsync(), await E2EServer.StartWithReleaseFeedAsync(feed, version, runtime), "About");

    [E2EFact]
    public async Task A_newer_release_in_the_feed_is_named_with_its_notes_and_installer_and_Check_again_reads_the_feed_again()
    {
        using var feed = new FakeReleaseFeed();
        await using var system = await OpenAsync(feed, Older, "source");
        var page = system.Page;
        await Expect(Status(page)).ToContainTextAsync("Update available");
        await Expect(Status(page)).ToContainTextAsync($"Weir v{FakeReleaseFeed.NewestVersion} is available (you have v{Older})");
        await Expect(Updates(page).GetByRole(AriaRole.Link, new() { Name = "Release notes →", Exact = true })).ToHaveAttributeAsync(
            "href",
            new Regex($@"^https://github.com/[^/]+/Weir/releases/tag/v{Regex.Escape(FakeReleaseFeed.NewestVersion)}$"));
        await Expect(Updates(page).GetByRole(AriaRole.Link, new() { Name = "Download installer →", Exact = true })).ToHaveAttributeAsync(
            "href",
            new Regex($@"^https://github.com/[^/]+/Weir/releases/download/v{Regex.Escape(FakeReleaseFeed.NewestVersion)}/Weir-win-Setup.exe$"));
        await Expect(CheckAgain(page)).ToHaveTextAsync("Check again →");
        await PageTrail.WatchAsync(page, "checking", "[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Checking...' && b.disabled)");
        var reads = ReadsSent.Watch(page);
        var (mark, feedReads) = (reads.Mark(), feed.Reads);

        await page.RunAndWaitForResponseAsync(() => CheckAgain(page).ClickAsync(), response => response.Url.EndsWith(UpdateStatus, StringComparison.Ordinal));

        await Expect(CheckAgain(page)).ToHaveTextAsync("Check again →");
        Assert.True(await PageTrail.SawAsync(page, "checking"), "The button never said it was checking while it was off.");
        Assert.Single(reads.Since(mark, UpdateStatus));
        Assert.Equal(feedReads + 1, feed.Reads);
        await Expect(Status(page)).ToContainTextAsync($"Weir v{FakeReleaseFeed.NewestVersion} is available (you have v{Older})");
    }

    [E2EFact]
    public async Task The_release_the_install_runs_is_up_to_date_and_offers_nothing_to_take()
    {
        using var feed = new FakeReleaseFeed();
        await using var system = await OpenAsync(feed, FakeReleaseFeed.NewestVersion, "docker");
        var page = system.Page;

        await Expect(Status(page)).ToContainTextAsync("Up to date");
        await Expect(Status(page)).ToContainTextAsync($"You have Weir v{FakeReleaseFeed.NewestVersion}");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What happens next", Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy the update command", Exact = true })).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task A_feed_that_cannot_be_read_says_Weir_could_not_check_and_Check_again_says_the_same_until_it_can()
    {
        using var feed = new FakeReleaseFeed();
        feed.GoUnavailable();
        await using var system = await OpenAsync(feed, Older, "source");
        var page = system.Page;

        await Expect(Status(page)).ToContainTextAsync("Unavailable");
        await Expect(Status(page)).ToContainTextAsync("Could not check for updates right now. Weir could not reach GitHub.");
        var feedReads = feed.Reads;
        Assert.True(feedReads > 0, "Weir never asked the feed.");

        await page.RunAndWaitForResponseAsync(() => CheckAgain(page).ClickAsync(), response => response.Url.EndsWith(UpdateStatus, StringComparison.Ordinal));

        await Expect(CheckAgain(page)).ToHaveTextAsync("Check again →");
        await Expect(Status(page)).ToContainTextAsync("Could not check for updates right now. Weir could not reach GitHub.");
        Assert.True(feed.Reads > feedReads, "Check again did not ask the feed again.");
    }

    [E2EFact]
    public async Task A_Docker_install_is_given_the_command_to_run_and_Copy_puts_it_on_the_clipboard_and_says_Copied_for_two_seconds()
    {
        using var feed = new FakeReleaseFeed();
        var page = await NewPageAsync();
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        // The page runs on a clock the test can move, from before its first timer is set.
        await page.Clock.InstallAsync();
        await using var system = await OpenAsync(feed, Older, "docker", page);
        var next = page.GetByRole(AriaRole.Region, new() { Name = "What happens next", Exact = true });
        await Expect(next).ToContainTextAsync(DockerCommand);
        await Expect(next).ToContainTextAsync("Keep the same WEIR_HOME volume and WEIR_SESSION_SECRET value across upgrades so browser sessions and setup state continue cleanly.");
        var copy = next.GetByRole(AriaRole.Button, new() { Name = "Copy the update command", Exact = true });
        await Expect(copy).ToHaveTextAsync("Copy →");

        await copy.ClickAsync();

        await Expect(copy).ToHaveTextAsync("Copied");
        Assert.Equal(DockerCommand, await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));

        await page.Clock.RunForAsync(2_000);

        await Expect(copy).ToHaveTextAsync("Copy →");
    }
}
