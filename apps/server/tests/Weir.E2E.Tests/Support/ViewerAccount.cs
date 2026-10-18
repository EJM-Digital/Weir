using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>A viewer, who can look at every screen and change none: the account is added next to the administrator's, and signs in through the sign-in page.</summary>
public static class ViewerAccount
{
    /// <summary>Adds the viewer (the administrator must already exist, or Weir would take the first sign-up for itself) and signs the page in as them.</summary>
    public static async Task SignInAsync(IPage page, string baseUrl, string databasePath)
    {
        E2EDatabase.AddUser(databasePath, SeededAccounts.ViewerUsername, SeededAccounts.ViewerPasswordHash, "viewer");
        await page.GotoAsync($"{baseUrl.TrimEnd('/')}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var login = page.GetByTestId("login-username");
        await Expect(login).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await login.FillAsync(SeededAccounts.ViewerUsername);
        await page.GetByTestId("login-password").FillAsync(SeededAccounts.ViewerPassword);
        await page.GetByTestId("login-submit").ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
    }
}
