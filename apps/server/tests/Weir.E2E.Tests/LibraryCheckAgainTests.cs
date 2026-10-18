using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// "Check again" on the Library page in a real browser: it asks the server for a check, says it is starting one while the server takes
/// the request, stays off while a check runs, and the counts come back when it is done. When the page cannot reach Weir it says so.
/// </summary>
public sealed class LibraryCheckAgainTests(E2EServer server) : E2ETestBase(server)
{
    private const float ServerMs = 60_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Arrival = "Arrival (2016)/Arrival (2016).mkv";

    [E2EFact]
    public async Task Check_again_asks_the_server_once_says_Starting_a_check_then_stays_off_while_it_runs_and_the_new_counts_arrive_without_a_reload()
    {
        await using var rig = await LibraryRig.StartAsync();
        await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
        await rig.UseRulesKeepingAudioAsync("English only", "eng");
        await rig.ScanAsync();
        var page = await NewPageAsync(Desktop);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var check = page.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true });
        var scan = page.GetByTestId("library-scan");
        await Expect(check).ToBeEnabledAsync();
        await rig.AddFilmAsync("Heat (1995)/Heat (1995).mkv", ["eng", "fre"], []);
        await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: true);
        var mark = log.Mark();

        using (rig.HoldWrites())
        {
            await check.ClickAsync();

            var starting = page.GetByRole(AriaRole.Button, new() { Name = "Starting a check…", Exact = true });
            await Expect(starting).ToBeDisabledAsync();
        }

        await Expect(scan).ToContainTextAsync("Checking this workflow now", new() { Timeout = ServerMs });
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true })).ToBeDisabledAsync();
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-scan", sent.PathAndQuery, StringComparison.Ordinal);

        await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: false);

        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(2, new() { Timeout = ServerMs });
        await Expect(scan).ToContainTextAsync("checked just now", new() { Timeout = ServerMs });
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true })).ToBeEnabledAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    [E2EFact]
    public async Task When_the_server_refuses_Check_again_says_the_check_could_not_be_started_in_an_alert_and_the_button_works_again()
    {
        await using var rig = await LibraryRig.StartAsync(writesGiveUpQuickly: true);
        await rig.AddFilmAsync(Arrival, ["eng", "fre"], []);
        await rig.ScanAsync();
        var page = await NewPageAsync(Desktop);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/library?library={rig.LibraryId}");
        await Expect(page.GetByTestId("library-row")).ToHaveCountAsync(1, new() { Timeout = ServerMs });
        var check = page.GetByRole(AriaRole.Button, new() { Name = "Check again", Exact = true });
        var failure = page.GetByTestId("library-scan-error");
        var mark = log.Mark();

        using (rig.HoldWrites())
        {
            var refused = await page.RunAndWaitForResponseAsync(
                () => check.ClickAsync(),
                response => response.Url.EndsWith("/library-scan", StringComparison.Ordinal),
                new() { Timeout = ServerMs });

            await Expect(failure).ToHaveTextAsync(await BusyAnswer.DetailAsync(refused), new() { Timeout = ServerMs });
        }

        await Expect(failure).ToHaveAttributeAsync("role", "alert");
        await Expect(check).ToBeEnabledAsync();
        Assert.Empty(await rig.CleansAsync());

        await check.ClickAsync();

        await Expect(failure).ToHaveCountAsync(0, new() { Timeout = ServerMs });
        await Expect(page.GetByTestId("library-scan")).ToContainTextAsync("checked just now", new() { Timeout = ServerMs });
        Assert.All(log.Since(mark), sent => Assert.EndsWith($"/processing/libraries/{rig.LibraryId}/library-scan", sent.PathAndQuery, StringComparison.Ordinal));
    }
}
