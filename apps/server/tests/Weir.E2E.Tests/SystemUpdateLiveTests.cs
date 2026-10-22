using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › About follows the update the tray checks for, downloads and installs, and hands the person's button presses to
/// the tray, with no reload. The tray is not started by these tests: it tells the server where it is with an update by writing
/// a file in Weir's data folder and hears a button through a flag file there, so the tests play the tray with those files
/// (<see cref="TrayFiles"/>), and stopping and starting the server is what the tray does to install. Each test has a server of
/// its own, set up as the Windows package, so restarting it disturbs no other test.
/// </summary>
public sealed class SystemUpdateLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float NoticeMs = 10_000;
    private const float RecoverMs = 40_000;

    private const string ReadyNotice = "Update ready to install — v9.9.9";
    private const string RestartingNote = "Weir is restarting to finish the update.";
    private const string Version = "9.9.9";

    /// <summary>Signs in to <paramref name="own"/> and opens System › About with the live stream open, its tray alive.</summary>
    private async Task<IPage> OpenAboutAsync(WeirServer own)
    {
        TrayFiles.WriteHeartbeat(own.Home);
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, baseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Navigation.OpenTabAsync(page, "System", "About");
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        return page;
    }

    private static async Task AssertNotReloadedAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

    private static ILocator Notice(IPage page) => page.GetByText("Update ready to install", new() { Exact = false });

    private static ILocator Restarting(IPage page) => page.GetByText(RestartingNote, new() { Exact = false });

    private static ILocator Button(IPage page, string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static ILocator RestartButton(IPage page) => Button(page, "Restart and apply");

    [E2EFact]
    public async Task An_update_the_tray_downloaded_shows_its_notice_and_clears_it_without_a_reload()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var page = await OpenAboutAsync(own);
        await Expect(Notice(page)).ToHaveCountAsync(0);

        TrayFiles.WriteUpdateState(own.Home, downloaded: true, version: "9.9.9");

        await Expect(page.GetByText(ReadyNotice, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await Expect(RestartButton(page)).ToBeEnabledAsync();

        TrayFiles.ClearUpdateState(own.Home);

        await Expect(Notice(page)).ToHaveCountAsync(0, new() { Timeout = NoticeMs });
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task After_restart_to_apply_the_page_recovers_when_the_server_is_back_without_a_reload()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var page = await OpenAboutAsync(own);
        TrayFiles.WriteUpdateState(own.Home, downloaded: true, version: "9.9.9");
        await Expect(page.GetByText(ReadyNotice, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await RestartButton(page).ClickAsync();
        await Expect(Restarting(page)).ToBeVisibleAsync();

        // What the tray does with the flag the click wrote: installs the update, which clears its file, and starts Weir again.
        TrayFiles.ClearUpdateState(own.Home);
        await own.RestartAsync(samePort: true);

        await Expect(page.GetByTestId("live-connection-banner")).ToHaveCountAsync(0, new() { Timeout = RecoverMs });
        await Expect(Notice(page)).ToHaveCountAsync(0, new() { Timeout = RecoverMs });
        await Expect(Restarting(page)).ToHaveCountAsync(0);
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task After_a_restart_that_did_not_install_the_update_the_notice_offers_the_restart_again()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var page = await OpenAboutAsync(own);
        TrayFiles.WriteUpdateState(own.Home, downloaded: true, version: "9.9.9");
        await Expect(page.GetByText(ReadyNotice, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await RestartButton(page).ClickAsync();
        await Expect(Restarting(page)).ToBeVisibleAsync();

        await own.RestartAsync(samePort: true);

        await Expect(Restarting(page)).ToHaveCountAsync(0, new() { Timeout = RecoverMs });
        await Expect(RestartButton(page)).ToBeEnabledAsync();
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task While_update_checking_is_off_Check_now_and_Download_update_still_go_to_the_tray()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync(checksForUpdates: false);
        var page = await OpenAboutAsync(own);
        await Expect(page.GetByTestId("suite-settings-release-status")).ToContainTextAsync("Off");
        await Expect(Button(page, "Check now")).ToBeEnabledAsync();

        await Button(page, "Check now").ClickAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.CheckRequest);
        TrayFiles.WriteUpdateStep(own.Home, "idle", Version, checkedAt: DateTimeOffset.UtcNow);
        await Expect(Button(page, "Download update")).ToBeEnabledAsync(new() { Timeout = NoticeMs });
        await Expect(page.GetByTestId("suite-settings-release-status")).ToContainTextAsync($"Weir v{Version} is available", new() { Timeout = NoticeMs });
        await Expect(page.GetByTestId("suite-settings-manual-check")).ToContainTextAsync("Update checking is off");

        await Button(page, "Download update").ClickAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.DownloadRequest);
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task While_update_checking_is_off_a_Check_now_that_finds_nothing_newer_says_so_and_still_says_checking_is_off()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync(checksForUpdates: false);
        var page = await OpenAboutAsync(own);
        var line = page.GetByTestId("suite-settings-release-status");
        await Expect(line).ToContainTextAsync("Update checking is off");

        await Button(page, "Check now").ClickAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.CheckRequest);
        TrayFiles.WriteUpdateStep(own.Home, "checking");
        TrayFiles.WriteUpdateStep(own.Home, "idle", checkedAt: DateTimeOffset.UtcNow);

        await Expect(line).ToContainTextAsync("Up to date", new() { Timeout = NoticeMs });
        await Expect(line).ToContainTextAsync("You have Weir v");
        await Expect(page.GetByTestId("suite-settings-manual-check")).ToContainTextAsync("Update checking is off, so Weir will not look again by itself.");
        await Expect(Button(page, "Download update")).ToBeDisabledAsync();
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task Check_now_then_Download_update_then_Restart_and_apply_follow_the_tray_without_a_reload()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var page = await OpenAboutAsync(own);
        await Expect(Button(page, "Check now")).ToBeEnabledAsync();
        await Expect(Button(page, "Download update")).ToBeDisabledAsync();
        await Expect(RestartButton(page)).ToBeDisabledAsync();

        // Check now: the click shows at once; the tray takes the request, checks, and finds an update.
        await Button(page, "Check now").ClickAsync();
        await Expect(Button(page, "Checking…")).ToBeDisabledAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.CheckRequest);
        TrayFiles.WriteUpdateStep(own.Home, "checking");
        await Expect(Button(page, "Checking…")).ToBeDisabledAsync();
        TrayFiles.WriteUpdateStep(own.Home, "idle", Version);
        await Expect(Button(page, "Download update")).ToBeEnabledAsync(new() { Timeout = NoticeMs });
        await Expect(RestartButton(page)).ToBeDisabledAsync();

        // Download update: the tray downloads, whatever the update mode, and the page shows it live, then the ready notice.
        await Button(page, "Download update").ClickAsync();
        await Expect(Button(page, "Downloading update…")).ToBeDisabledAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.DownloadRequest);
        TrayFiles.WriteUpdateStep(own.Home, "downloading", Version);
        await Expect(page.GetByText($"Downloading the update — v{Version}", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await Expect(Button(page, "Check now")).ToBeDisabledAsync();
        TrayFiles.WriteUpdateStep(own.Home, "downloaded", Version);
        await Expect(page.GetByText(ReadyNotice, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await Expect(RestartButton(page)).ToBeEnabledAsync();
        await Expect(Button(page, "Check now")).ToBeDisabledAsync();
        await Expect(Button(page, "Download update")).ToBeDisabledAsync();

        // Restart and apply: the tray installs the update, which clears its file, and starts Weir again.
        await RestartButton(page).ClickAsync();
        await Expect(Restarting(page)).ToBeVisibleAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.ApplyRequest);
        TrayFiles.ClearUpdateState(own.Home);
        await own.RestartAsync(samePort: true);

        await Expect(page.GetByTestId("live-connection-banner")).ToHaveCountAsync(0, new() { Timeout = RecoverMs });
        await Expect(Notice(page)).ToHaveCountAsync(0, new() { Timeout = RecoverMs });
        await Expect(Button(page, "Check now")).ToBeEnabledAsync(new() { Timeout = RecoverMs });
        await AssertNotReloadedAsync(page);
    }

    [E2EFact]
    public async Task With_no_tray_to_answer_the_buttons_are_off_and_say_why()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, baseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        TrayFiles.WriteUpdateStep(own.Home, "idle", Version);
        await Navigation.OpenTabAsync(page, "System", "About");

        await Expect(page.GetByText("The Weir tray isn't running, so Weir can't update itself from here.", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        foreach (var name in new[] { "Check now", "Download update", "Restart and apply" })
        {
            await Expect(Button(page, name)).ToBeDisabledAsync();
        }
    }

    [E2EFact]
    public async Task A_check_or_download_that_fails_says_why_and_offers_the_steps_again()
    {
        await using var own = await E2EServer.StartWindowsInstallAsync();
        var page = await OpenAboutAsync(own);
        const string Reason = "Weir could not download the update. Check the internet connection and try again.";
        TrayFiles.WriteUpdateStep(own.Home, "idle", Version);
        await Expect(Button(page, "Download update")).ToBeEnabledAsync(new() { Timeout = NoticeMs });

        await Button(page, "Download update").ClickAsync();
        await TrayFiles.TakeRequestAsync(own.Home, TrayFiles.DownloadRequest);
        TrayFiles.WriteUpdateStep(own.Home, "downloading", Version);
        TrayFiles.WriteUpdateStep(own.Home, "failed", Version, Reason);

        await Expect(page.GetByText(Reason, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = NoticeMs });
        await Expect(Button(page, "Download update")).ToBeEnabledAsync();
        await Expect(Button(page, "Check now")).ToBeEnabledAsync();
        await AssertNotReloadedAsync(page);
    }
}
