using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Dashboard counts down and up on its own once a second between the server's updates: the Next tile's countdown and the age of a
/// connection's last test. Neither needs anything from the server to move.
/// </summary>
public sealed partial class DashboardClockTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    // The Next tile counts in seconds from the last 90.
    private const int LeadSeconds = 60;

    [GeneratedRegex(@"^(\d+) s$")]
    private static partial Regex Seconds();

    [GeneratedRegex(@"^(\d+)s ago")]
    private static partial Regex SecondsAgo();

    private static async Task<DateTimeOffset> NextLookAsync(ProcessingRig rig)
    {
        var listed = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries/{await rig.MoviesWorkflowIdAsync()}");
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return DateTimeOffset.Parse((string)listed.Fields["next_look_at"]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    [E2EFact]
    public async Task The_Next_countdown_runs_down_by_itself_between_server_updates()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var look = await NextLookAsync(rig);
            var page = await NewPageAsync();

            // The page's clock starts a minute before the look, so the countdown is in seconds; it then runs as any clock does.
            await page.Clock.InstallAsync(new() { TimeDate = look.UtcDateTime.AddSeconds(-LeadSeconds) });
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            var figure = page.GetByTestId("live-next-figure");
            await Expect(figure).ToHaveTextAsync(Seconds(), new() { Timeout = PushMs });
            var first = int.Parse(Seconds().Match(await figure.InnerTextAsync()).Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.InRange(first, 1, LeadSeconds);

            // It shows a smaller number a moment later with nobody asking the server: the page's own once-a-second tick moved it.
            var asked = 0;
            page.Request += (_, request) =>
            {
                if (request.Method == "GET" && request.Url.Contains("/api/v1/processing", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref asked);
                }
            };
            await page.WaitForFunctionAsync(
                "(first) => { const m = /^(\\d+) s$/.exec(document.querySelector('[data-testid=\"live-next-figure\"]')?.textContent ?? ''); return m !== null && Number(m[1]) <= first - 3; }",
                first,
                new() { Timeout = PushMs });
            Assert.Equal(0, Volatile.Read(ref asked));
        });
    }

    [E2EFact]
    public async Task A_connections_last_test_gets_older_by_the_second_after_Check_now()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync(new ViewportSize { Width = 1600, Height = 1000 });
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            var health = page.GetByTestId("live-health");
            await Expect(health).ToContainTextAsync("not tested yet", new() { Timeout = PushMs });

            await page.GetByRole(AriaRole.Region, new() { Name = "Health", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }).ClickAsync();

            await Expect(health.GetByRole(AriaRole.Status)).ToHaveTextAsync("Checked · all answered.", new() { Timeout = PushMs });
            var tested = health.Locator("time");
            await Expect(tested).ToHaveTextAsync(new Regex(@"^(just now|\d+s ago)"), new() { Timeout = PushMs });

            // Older by itself: the age counts up past the 'just now' of the first seconds with the server saying nothing more.
            await Expect(tested).ToHaveTextAsync(SecondsAgo(), new() { Timeout = PushMs });
            var first = int.Parse(SecondsAgo().Match(await tested.InnerTextAsync()).Groups[1].Value, CultureInfo.InvariantCulture);
            await page.WaitForFunctionAsync(
                "(first) => { const m = /^(\\d+)s ago/.exec(document.querySelector('[data-testid=\"live-health\"] time')?.textContent ?? ''); return m !== null && Number(m[1]) >= first + 2; }",
                first,
                new() { Timeout = PushMs });
        });
    }
}
