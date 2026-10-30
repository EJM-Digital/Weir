using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Security › Skip sign-in for devices on your home network, against the real server. The browsers here reach Weir over
/// loopback, which counts as the home network: with the setting on one that has never signed in loads the app, with it off one must
/// sign in, and a visitor who skipped sign-in leaves no session behind.
/// </summary>
public sealed class HomeNetworkSignInTests(E2EServer server) : E2ETestBase(server)
{
    private const string SwitchName = "Skip sign-in on the local network";

    private static ILocator Card(IPage page) => page.GetByTestId("suite-security-home-network");

    private async Task SaveAsync(IPage owner, bool enabled, string saved)
    {
        var card = Card(owner);
        var toggle = card.GetByRole(AriaRole.Switch, new() { Name = SwitchName });
        if (enabled)
        {
            await toggle.CheckAsync();
        }
        else
        {
            await toggle.UncheckAsync();
        }

        await card.GetByLabel("Current password", new() { Exact = true }).FillAsync(Navigation.BootstrapPassword);
        await card.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(card.GetByTestId("home-network-saved")).ToHaveTextAsync(saved, new() { Timeout = Navigation.SubmitSettleMs });
    }

    private async Task<IPage> OpenAnotherBrowserAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        return page;
    }

    [E2EFact]
    public async Task With_it_on_a_browser_that_never_signed_in_loads_the_app_and_with_it_off_it_must_sign_in()
    {
        var owner = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(owner, BaseUrl);
        await SecurityTab.OpenAsync(owner);
        await Expect(Card(owner).GetByRole(AriaRole.Switch, new() { Name = SwitchName })).Not.ToBeCheckedAsync();
        var signedIn = await OpenAnotherBrowserAsync();
        await Expect(signedIn.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        await SaveAsync(owner, enabled: true, "Devices on your local network now skip sign-in.");

        var skipped = await OpenAnotherBrowserAsync();
        await Expect(skipped.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(skipped.Context, BaseUrl));
        Assert.Empty(await skipped.Context.CookiesAsync());
        await skipped.GetByTestId("user-menu").ClickAsync();
        await Expect(skipped.GetByTestId("sign-out")).ToHaveCountAsync(0);
        Assert.Equal(1, await SecurityTab.RecordedEventCountAsync(owner.Context, BaseUrl, "system.home_network_sign_in.changed"));

        await SaveAsync(owner, enabled: false, "Every device signs in again, including ones on your local network.");

        var signInAgain = await OpenAnotherBrowserAsync();
        await Expect(signInAgain.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(signInAgain.Context, BaseUrl));
        Assert.Equal(2, await SecurityTab.RecordedEventCountAsync(owner.Context, BaseUrl, "system.home_network_sign_in.changed"));
    }

    [E2EFact]
    public async Task A_wrong_password_leaves_it_off_and_says_so()
    {
        var owner = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(owner, BaseUrl);
        await SecurityTab.OpenAsync(owner);
        var card = Card(owner);
        await card.GetByRole(AriaRole.Switch, new() { Name = SwitchName }).CheckAsync();
        await card.GetByLabel("Current password", new() { Exact = true }).FillAsync("not the password");

        await card.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(card.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Current password is not correct.", new() { Timeout = Navigation.SubmitSettleMs });
        var another = await OpenAnotherBrowserAsync();
        await Expect(another.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
    }
}
