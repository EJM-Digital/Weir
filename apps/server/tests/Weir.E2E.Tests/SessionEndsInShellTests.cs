using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The two ways a signed-in session ends from inside the app: the server ends it while the app is open, and the person signs out and the
/// server does not answer as it should.
/// </summary>
public sealed class SessionEndsInShellTests(E2EServer server) : E2ETestBase(server)
{
    private const string LogoutPattern = "**/api/v1/auth/logout";
    private const string SignOutFailed = "Weir couldn't sign you out, so you're still signed in. Try Sign out again.";

    [E2EFact]
    public async Task A_session_the_server_ended_sends_the_open_app_to_sign_in_saying_it_expired_without_reloading_the_page()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        // The browser keeps a note of a sign-in for ten seconds to tell a cookie it refused from a session that ended.
        await page.EvaluateAsync("() => sessionStorage.clear()");
        var documents = 0;
        page.Request += (_, request) =>
        {
            if (request.ResourceType == "document")
            {
                Interlocked.Increment(ref documents);
            }
        };

        E2EDatabase.EndAllSessions(Server.DatabasePath);
        await Navigation.OpenSidebarAsync(page, "Activity");

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login?session=expired");
        await Expect(page.GetByText("Your session expired. Sign in again to keep using Weir.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("shell-ready")).ToHaveCountAsync(0);
        Assert.Equal(0, Volatile.Read(ref documents));

        await SecurityTab.SignInAsync(page, BaseUrl, Navigation.BootstrapUser, Navigation.BootstrapPassword);
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("user-menu")).ToContainTextAsync(Navigation.BootstrapUser);
    }

    [E2EFact]
    public async Task Sign_out_that_the_server_refuses_says_the_person_is_still_signed_in_and_a_second_try_signs_out()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var refuse = true;
        await page.RouteAsync(LogoutPattern, async route =>
        {
            if (refuse)
            {
                // The server's own refusal: a token it does not know.
                var headers = new Dictionary<string, string>(route.Request.Headers) { ["x-csrf-token"] = "not-the-token" };
                await route.ContinueAsync(new() { Headers = headers });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("sign-out").ClickAsync();

        var notice = page.GetByTestId("sign-out-failed");
        await Expect(notice).ToHaveTextAsync(new Regex($"^{Regex.Escape(SignOutFailed)}"), new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = SignOutFailed })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync();
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));
        await Axe.AssertNoViolationsAsync(page, "The app after a sign-out the server refused");

        // Dismissing it only hides the sentence: the person is as signed in as before.
        await notice.GetByRole(AriaRole.Button, new() { Name = "Dismiss", Exact = true }).ClickAsync();
        await Expect(notice).ToHaveCountAsync(0);
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));

        refuse = false;
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("sign-out").ClickAsync();

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        // The sign-in page shows at once and the session ends on the server behind it.
        await Poll.UntilAsync(async () => await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl) == 401, "the server to end the session");
        await Expect(page.GetByTestId("sign-out-failed")).ToHaveCountAsync(0);
    }
}
