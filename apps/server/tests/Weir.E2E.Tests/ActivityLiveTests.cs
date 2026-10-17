using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Activity kept current by the server, in a real browser: the percent a running pass has written changes on its row as ffmpeg reports it,
/// a file that arrived while the live link was down is listed once the link is back with no reload, and "minutes ago" moves on by itself.
/// </summary>
public sealed class ActivityLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private const float PassMs = 180_000;

    // The longest the page may take to notice: the 5 s the stream asks the browser to wait before it tries again.
    private const float ReconnectMs = 20_000;

    private const string Film = "Harbour Lights 2024.mkv";

    [E2EFact]
    public async Task A_running_pass_shows_the_percent_ffmpeg_reports_and_it_moves_on_its_row_without_a_new_read_of_the_list()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenActivityAsync(rig);
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");

            // The film is 60 seconds long, so these are 25, 60 and 90 per cent.
            await rig.HandOffAsync("h-live", Film, [15.0, 36.0, 54.0]);

            var row = Row(page, Film);
            await Expect(row).ToContainTextAsync("Writing · 25%", new() { Timeout = PushMs });
            var mark = log.ReadMark();

            rig.AdvanceWrite(Film, 1);

            await Expect(row).ToContainTextAsync("Writing · 60%", new() { Timeout = PushMs });
            rig.AdvanceWrite(Film, 2);
            await Expect(row).ToContainTextAsync("Writing · 90%", new() { Timeout = PushMs });
            Assert.DoesNotContain(log.ReadsSince(mark), read => read.PathAndQuery.StartsWith("/api/v1/processing/files?", StringComparison.Ordinal) && read.PathAndQuery.Contains("sort=", StringComparison.Ordinal));
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

            rig.ReleasePass(Film);

            await Expect(row).ToContainTextAsync("Done", new() { Timeout = PassMs });
            await Expect(row).Not.ToContainTextAsync("Writing");
        });
    }

    [E2EFact]
    public async Task A_file_handed_over_while_the_live_link_was_down_is_listed_once_the_link_is_back_with_no_reload()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync();
            await page.RunAndWaitForResponseAsync(
                () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
                response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
            await page.GotoAsync($"{rig.BaseUrl}/activity");
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Files" })).ToContainTextAsync("Nothing yet.", new() { Timeout = PushMs });
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            var banner = page.GetByTestId("live-connection-banner");
            await page.Context.SetOfflineAsync(true);
            await Expect(banner).ToHaveTextAsync("Live updates paused: can't reach Weir. Reconnecting…", new() { Timeout = ReconnectMs });

            await rig.HandOffAsync("h-away", Film);
            await rig.WaitForWritesStartedAsync(1);
            await Expect(Row(page, Film)).ToHaveCountAsync(0);

            await page.Context.SetOfflineAsync(false);

            await Expect(banner).ToHaveCountAsync(0, new() { Timeout = ReconnectMs });
            await Expect(Row(page, Film)).ToContainTextAsync("Writing", new() { Timeout = ReconnectMs });
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
            rig.ReleasePass(Film);
        });
    }

    [E2EFact]
    public async Task The_When_column_moves_on_from_just_now_to_minutes_ago_by_itself()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            await rig.HandOffAsync("h-clock", Film);
            await rig.WaitForWritesStartedAsync(1);
            var page = await NewPageAsync();
            await page.Clock.InstallAsync();
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await page.GotoAsync($"{rig.BaseUrl}/activity");
            var when = Row(page, Film).Locator("td[data-col='when']");
            await Expect(when).ToHaveTextAsync("just now", new() { Timeout = PushMs });

            await page.Clock.FastForwardAsync("05:00");

            await Expect(when).ToHaveTextAsync("5 min ago", new() { Timeout = PushMs });

            await page.Clock.FastForwardAsync("02:00:00");

            await Expect(when).ToHaveTextAsync("2 h ago", new() { Timeout = PushMs });
            rig.ReleasePass(Film);
        });
    }

    private async Task<(IPage Page, RequestLog Log)> OpenActivityAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Files" })).ToContainTextAsync("Nothing yet.", new() { Timeout = PushMs });
        return (page, log);
    }

    private static ILocator Row(IPage page, string file) => page.GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = new Regex(Regex.Escape(file)) });
}
