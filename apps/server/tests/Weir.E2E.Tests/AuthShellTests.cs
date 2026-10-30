using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Bootstrap, setup wizard, app, logout, guard redirect.</summary>
public sealed partial class AuthShellTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Auth_shell_bootstrap_login_logout_guard()
    {
        var page = await NewPageAsync();

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page).ToHaveURLAsync(SetupUrl());

        await page.GetByTestId("setup-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("setup-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-submit").ClickAsync();

        // Bootstrap signs the new admin in directly (#704): no separate sign-in screen follows, only the recovery codes to save.
        await SetupWizard.SaveRecoveryCodesAsync(page);
        await Expect(page).ToHaveURLAsync(SetupWizardUrl());
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Set up Weir" })).ToBeVisibleAsync();

        // A new install asks how downloads reach Weir before it shows any folder.
        await Expect(page.GetByText("How do your downloads reach Weir?")).ToBeVisibleAsync();
        // The four ways downloads arrive, and Yes and No for skipping sign-in on the home network, which starts unanswered.
        await Expect(page.GetByRole(AriaRole.Radio)).ToHaveCountAsync(6);
        await Expect(page.GetByRole(AriaRole.Radio, new() { Checked = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Movies watched folder" })).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Radio, new() { NameRegex = Neither() }).CheckAsync();
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Movies watched folder" })).ToBeVisibleAsync();
        await page.GetByTestId("setup-wizard-skip").ClickAsync();

        await Expect(page).ToHaveURLAsync(ShellUrl());
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync();

        // Sign out is in the user menu at the foot of the side menu.
        await page.GetByTestId("user-menu").ClickAsync();
        var logoutResponse = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByTestId("sign-out").ClickAsync(),
            response => response.Url.EndsWith("/api/v1/auth/logout", StringComparison.Ordinal),
            new() { Timeout = Navigation.UrlAssertMs });
        Assert.True(logoutResponse.Ok);
        await Expect(page).ToHaveURLAsync(LoginUrl(), new() { Timeout = Navigation.UrlAssertMs });

        await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page).ToHaveURLAsync(LoginUrl(), new() { Timeout = Navigation.UrlAssertMs });
    }

    [GeneratedRegex(".*/setup")]
    private static partial Regex SetupUrl();

    [GeneratedRegex(".*/setup-wizard")]
    private static partial Regex SetupWizardUrl();

    [GeneratedRegex("Neither")]
    private static partial Regex Neither();

    [GeneratedRegex(@".*/(?:$|[/?#])")]
    private static partial Regex ShellUrl();

    [GeneratedRegex(".*/login")]
    private static partial Regex LoginUrl();
}
