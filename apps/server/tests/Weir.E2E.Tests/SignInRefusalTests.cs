using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>A wrong password on the sign-in page is refused in the server's words, and the form stays usable.</summary>
public sealed class SignInRefusalTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task A_wrong_password_shows_the_servers_reason_keeps_the_form_and_the_right_one_then_signs_in()
    {
        using var client = new WeirClient(new Uri(BaseUrl));
        await client.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var username = page.GetByTestId("login-username");
        var password = page.GetByTestId("login-password");

        await username.FillAsync(Navigation.BootstrapUser);
        await password.FillAsync("not-the-password");
        await page.GetByTestId("login-submit").ClickAsync();

        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Invalid username or password.", new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login");
        await Expect(username).ToHaveValueAsync(Navigation.BootstrapUser);
        await Expect(password).ToHaveValueAsync("not-the-password");
        await Expect(page.GetByTestId("login-submit")).ToBeEnabledAsync();

        await password.FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("login-submit").ClickAsync();

        await Expect(username).ToBeHiddenAsync(new() { Timeout = Navigation.UrlAssertMs });
    }
}
