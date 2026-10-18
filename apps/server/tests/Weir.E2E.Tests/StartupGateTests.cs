using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The "Starting Weir..." page the app shows until the server says it is ready: what it says while Weir is starting and while it does not
/// answer, how it keeps asking, how it ends when Weir never becomes ready, and that the app opens by itself the moment it does.
/// </summary>
public sealed class StartupGateTests(E2EServer server) : E2ETestBase(server)
{
    // The answer /ready gives while Weir is starting: 503, not ready, with the status "starting".
    private const string Starting = """{"ready":false,"status":"starting"}""";
    private const string ReadyPattern = "**/ready";
    private static readonly Regex StartingTitle = new("^Starting Weir");

    [E2EFact]
    public async Task While_Weir_is_starting_the_page_says_so_keeps_asking_and_opens_the_app_the_moment_it_is_ready()
    {
        var page = await NewPageAsync();
        var asked = 0;
        var starting = true;
        await page.RouteAsync(ReadyPattern, async route =>
        {
            Interlocked.Increment(ref asked);
            if (starting)
            {
                await route.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = Starting });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var title = page.GetByRole(AriaRole.Heading, new() { Level = 1 });
        await Expect(title).ToHaveTextAsync(StartingTitle);
        await Expect(page.GetByText("Waiting for Weir to start.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Progressbar, new() { Name = "Starting Weir" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Try again" })).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("shell-ready")).ToHaveCountAsync(0);
        await Axe.AssertNoViolationsAsync(page, "The starting page");

        // It asks again every second until Weir answers ready, with nothing for the person to press.
        await Poll.UntilAsync(() => Task.FromResult(Volatile.Read(ref asked) >= 3), "the page to ask /ready again while Weir was starting");

        starting = false;
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(title).Not.ToHaveTextAsync(StartingTitle);
    }

    [E2EFact]
    public async Task While_Weir_does_not_answer_the_page_says_it_is_waiting_for_the_server_and_opens_the_app_when_it_does()
    {
        var page = await NewPageAsync();
        var reachable = false;
        await page.RouteAsync(ReadyPattern, async route =>
        {
            if (reachable)
            {
                await route.ContinueAsync();
            }
            else
            {
                await route.AbortAsync("connectionrefused");
            }
        });

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync(StartingTitle);
        await Expect(page.GetByText("Waiting for the Weir server to answer.", new() { Exact = true })).ToBeVisibleAsync();

        reachable = true;
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
    }

    [E2EFact]
    public async Task A_Weir_that_never_becomes_ready_ends_the_wait_after_a_minute_and_Try_again_asks_afresh()
    {
        var page = await NewPageAsync();
        var starting = true;
        await page.RouteAsync(ReadyPattern, async route =>
        {
            if (starting)
            {
                await route.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = Starting });
            }
            else
            {
                await route.ContinueAsync();
            }
        });
        // The minute the page waits is the browser's clock, moved on by the test instead of being sat through.
        await page.Clock.InstallAsync();

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByText("Waiting for Weir to start.", new() { Exact = true })).ToBeVisibleAsync();

        await page.Clock.FastForwardAsync(61_000);

        await Expect(page.GetByText("Waiting for Weir to start.", new() { Exact = true })).ToBeVisibleAsync();
        var tryAgain = page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true });
        await Expect(tryAgain).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Progressbar, new() { Name = "Starting Weir" }).Locator("div")).ToHaveCSSAsync("width", new Regex(@"^\d"));
        await Axe.AssertNoViolationsAsync(page, "The starting page after the wait ran out");

        starting = false;
        await tryAgain.ClickAsync();
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
    }
}
