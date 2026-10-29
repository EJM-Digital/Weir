using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Recovery codes through the browser against the real server: setup shows ten and waits until they are saved, a forgotten password is
/// got past with one of them, and the new password signs in. The codes, the page's copy and its download are read from the screen.
/// </summary>
public sealed partial class RecoveryCodesTests(E2EServer server) : E2ETestBase(server)
{
    private const string NewPassword = "e2e-recovered-pass-min8";

    [E2EFact]
    public async Task Setup_waits_for_the_codes_to_be_saved_and_a_code_then_gets_the_owner_back_in_with_a_new_password()
    {
        var context = await NewContextAsync(viewport: null);
        await context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await context.NewPageAsync();
        ApplyDefaultTimeout(page);
        await page.GotoAsync($"{BaseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("setup-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("setup-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-submit").ClickAsync();

        // Setup waits here: nothing continues until the codes are marked as saved.
        await Expect(page.GetByTestId("setup-codes")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        await Axe.AssertNoViolationsAsync(page, "The recovery codes shown at setup");
        var codes = await page.GetByTestId("recovery-codes-list").GetByRole(AriaRole.Listitem).AllInnerTextsAsync();
        Assert.Equal(10, codes.Count);
        Assert.All(codes, code => Assert.Matches(CodeShape(), code));
        var continueButton = page.GetByTestId("recovery-codes-continue");
        await Expect(continueButton).ToBeDisabledAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy the recovery codes", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy the recovery codes", Exact = true })).ToHaveTextAsync("Copied");
        var copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(string.Join("\n", codes), copied.ReplaceLineEndings("\n"));
        var download = await page.RunAndWaitForDownloadAsync(async () => await page.GetByTestId("recovery-codes-download").ClickAsync());
        Assert.Equal("weir-recovery-codes.txt", download.SuggestedFilename);
        var saved = await File.ReadAllTextAsync((await download.PathAsync())!);
        Assert.All(codes, code => Assert.Contains(code, saved, StringComparison.Ordinal));
        await Expect(continueButton).ToBeDisabledAsync();

        await page.GetByTestId("recovery-codes-saved").CheckAsync();
        await continueButton.ClickAsync();
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });

        // The wizard that follows the codes asks whether devices on the home network may skip sign-in: it starts unanswered, and No is kept.
        var homeNetwork = page.GetByRole(AriaRole.Radio, new() { Name = "Skip sign-in for devices on your home network? No", Exact = true });
        await Expect(page.GetByRole(AriaRole.Radio, new() { Name = "Skip sign-in for devices on your home network? Yes", Exact = true })).Not.ToBeCheckedAsync();
        await homeNetwork.CheckAsync();
        await Expect(homeNetwork).ToBeCheckedAsync();
        await SetupWizard.Skip(page).ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(page.GetByTestId("recovery-prompt")).ToHaveCountAsync(0);
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("sign-out").ClickAsync();
        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        await page.GetByTestId("login-forgot").ClickAsync();
        await Expect(page).ToHaveURLAsync(RecoverUrl());
        await page.GetByTestId("recover-code").FillAsync($"  {codes[4].ToLowerInvariant()}  ");
        await page.GetByTestId("recover-code-submit").ClickAsync();
        await Expect(page.GetByTestId("recover-username")).ToHaveTextAsync(Navigation.BootstrapUser, new() { Timeout = Navigation.SubmitSettleMs });
        await Axe.AssertNoViolationsAsync(page, "Choosing a new password after a recovery code");
        await page.GetByTestId("recover-password").FillAsync(NewPassword);
        await page.GetByTestId("recover-confirm-password").FillAsync($"{NewPassword}-different");
        await page.GetByTestId("recover-password-submit").ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Passwords do not match.");
        await page.GetByTestId("recover-confirm-password").FillAsync(NewPassword);
        await page.GetByTestId("recover-password-submit").ClickAsync();

        await Expect(page).ToHaveURLAsync(LoginAfterChange(), new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(page.GetByText("Password changed. Sign in with your new password.")).ToBeVisibleAsync();
        await page.GetByTestId("login-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("login-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("login-submit").ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Invalid username or password.", new() { Timeout = Navigation.SubmitSettleMs });
        await page.GetByTestId("login-password").FillAsync(NewPassword);
        await page.GetByTestId("login-submit").ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });

        // The code is spent: the same one gets nobody back in again, and nine are left.
        await page.GotoAsync($"{BaseUrl}/system?tab=security", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("recovery-left")).ToHaveTextAsync("9 recovery codes left", new() { Timeout = Navigation.UrlAssertMs });
    }

    [GeneratedRegex(@"^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){3}$")]
    private static partial Regex CodeShape();

    [GeneratedRegex(".*/recover$")]
    private static partial Regex RecoverUrl();

    [GeneratedRegex(".*/login\\?password=changed$")]
    private static partial Regex LoginAfterChange();
}
