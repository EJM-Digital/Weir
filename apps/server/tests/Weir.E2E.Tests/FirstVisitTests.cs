using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Weir says a session expired only to someone who had one: a first visit goes quietly to account creation or sign-in.</summary>
public sealed class FirstVisitTests(E2EServer server) : E2ETestBase(server)
{
    private const string ExpiredMessage = "Your session expired. Sign in again to keep using Weir.";

    // Remembers every moment the page said it, so a message shown for an instant is found as surely as one that stays.
    private const string WatchForExpiredMessage =
        """
        window.__saidExpired = false;
        new MutationObserver(() => {
            if (document.body && /session expired/i.test(document.body.innerText)) window.__saidExpired = true;
        }).observe(document, { childList: true, subtree: true, characterData: true });
        """;

    private static async Task<bool> SaidExpiredAsync(IPage page) => await page.EvaluateAsync<bool>("() => window.__saidExpired === true");

    private async Task<IPage> NewWatchingPageAsync()
    {
        var page = await NewPageAsync();
        await page.AddInitScriptAsync(WatchForExpiredMessage);
        return page;
    }

    [E2EFact]
    public async Task A_fresh_install_s_first_page_is_account_creation_and_never_says_a_session_expired()
    {
        var page = await NewWatchingPageAsync();

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/setup");
        Assert.False(await SaidExpiredAsync(page), "A first visit was told a session expired.");
    }

    [E2EFact]
    public async Task A_browser_that_never_signed_in_to_an_install_with_an_account_gets_a_plain_sign_in()
    {
        using var client = new WeirClient(new Uri(BaseUrl));
        await client.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
        var page = await NewWatchingPageAsync();

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login");
        Assert.False(await SaidExpiredAsync(page), "A browser that never signed in was told a session expired.");
    }

    [E2EFact]
    public async Task A_session_that_ended_still_says_so_on_the_sign_in_page()
    {
        var page = await NewWatchingPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        Assert.False(await SaidExpiredAsync(page));
        // The browser keeps a note of a sign-in for ten seconds to tell a cookie it refused from a session that ended: this
        // session has been in use for longer than that.
        await page.EvaluateAsync("() => sessionStorage.clear()");

        E2EDatabase.EndAllSessions(Server.DatabasePath);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByText(ExpiredMessage, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login?session=expired");
    }

    [E2EFact]
    public async Task Signing_out_on_purpose_does_not_read_as_an_expired_session_on_the_next_visit()
    {
        var page = await NewWatchingPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("sign-out").ClickAsync();
        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        Assert.False(await SaidExpiredAsync(page), "Signing out was read as an expired session.");
    }
}
