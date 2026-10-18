using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The first-run page, /setup, against the real server: what creating the account sends and leaves behind, what is refused before
/// anything is sent and what the server refuses, the links to and from sign-in, and the password boxes.
/// </summary>
public sealed class CreateAccountTests(E2EServer server) : E2ETestBase(server)
{
    private const string BootstrapPath = "/api/v1/auth/bootstrap";
    private const string StrongPassword = "e2e-first-account-pass";

    private async Task<IPage> OpenSetupAsync(ViewportSize? viewport = null)
    {
        var page = await NewPageAsync(viewport);
        await page.GotoAsync($"{BaseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        return page;
    }

    private static async Task FillAsync(IPage page, string username, string password, string confirmation)
    {
        await page.GetByTestId("setup-username").FillAsync(username);
        await page.GetByTestId("setup-password").FillAsync(password);
        await page.GetByTestId("setup-confirm-password").FillAsync(confirmation);
    }

    private async Task<bool> AccountCreationOpenAsync(IBrowserContext context)
    {
        await using var response = await context.APIRequest.GetAsync($"{BaseUrl}/api/v1/auth/bootstrap/status");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("bootstrap_allowed").GetBoolean();
    }

    [E2EFact]
    public async Task Creating_the_account_sends_the_trimmed_name_signs_in_and_closes_account_creation()
    {
        var page = await OpenSetupAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create your account", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("setup-code")).ToHaveCountAsync(0);
        await Axe.AssertNoViolationsAsync(page, "The first-run page");
        await FillAsync(page, $"  {Navigation.BootstrapUser}  ", Navigation.BootstrapPassword, Navigation.BootstrapPassword);

        var created = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByTestId("setup-submit").ClickAsync(),
            request => request.Method == "POST" && request.Url.EndsWith(BootstrapPath, StringComparison.Ordinal));

        var sent = JsonDocument.Parse(created.PostData!).RootElement;
        Assert.Equal(Navigation.BootstrapUser, sent.GetProperty("username").GetString());
        Assert.Equal(Navigation.BootstrapPassword, sent.GetProperty("password").GetString());
        Assert.False(string.IsNullOrEmpty(sent.GetProperty("csrf_token").GetString()));
        Assert.False(sent.TryGetProperty("setup_code", out _), "A code was sent from the device Weir runs on.");
        await Expect(page).ToHaveURLAsync(new Regex(".*/setup-wizard"), new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync();
        await using var me = await page.Context.APIRequest.GetAsync($"{BaseUrl}/api/v1/auth/me");
        var account = JsonDocument.Parse(await me.TextAsync()).RootElement.GetProperty("user");
        Assert.Equal(Navigation.BootstrapUser, account.GetProperty("username").GetString());
        Assert.Equal("admin", account.GetProperty("role").GetString());
        Assert.Equal("pending", (string)(await SetupState.SettingsAsync(page.Context, BaseUrl))["setup_wizard_state"]!);
        Assert.False(await AccountCreationOpenAsync(page.Context));

        var another = await NewPageAsync();
        await another.GotoAsync($"{BaseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(another.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(another).ToHaveURLAsync($"{BaseUrl}/login");
    }

    [E2EFact]
    public async Task A_blank_name_and_passwords_that_differ_are_refused_before_anything_is_sent_and_typing_clears_the_reason()
    {
        var page = await OpenSetupAsync();
        var requests = RequestLog.Watch(page);
        var mark = requests.Mark();

        await FillAsync(page, "   ", StrongPassword, StrongPassword);
        await page.GetByTestId("setup-submit").ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Admin username is required.");

        await page.GetByTestId("setup-username").FillAsync("owner");
        await Expect(SetupWizard.Alert(page)).ToHaveCountAsync(0);
        await page.GetByTestId("setup-confirm-password").FillAsync($"{StrongPassword}-different");
        await page.GetByTestId("setup-submit").ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Passwords do not match.");
        await Axe.AssertNoViolationsAsync(page, "The first-run page with a reason shown");

        await page.GetByTestId("setup-confirm-password").FillAsync(StrongPassword);
        await Expect(SetupWizard.Alert(page)).ToHaveCountAsync(0);
        requests.AssertNothingSentSince(mark);
        Assert.True(await AccountCreationOpenAsync(page.Context));
    }

    [E2EFact]
    public async Task A_password_shorter_than_eight_characters_is_stopped_by_the_browser_and_nothing_is_sent()
    {
        var page = await OpenSetupAsync();
        var requests = RequestLog.Watch(page);
        var mark = requests.Mark();
        await FillAsync(page, "owner", "short12", "short12");

        await page.GetByTestId("setup-submit").ClickAsync();

        await Expect(page.Locator("[data-testid=setup-password]:invalid")).ToHaveCountAsync(1);
        requests.AssertNothingSentSince(mark);
        Assert.True(await AccountCreationOpenAsync(page.Context));
    }

    [E2EFact]
    public async Task The_server_refuses_a_weak_password_in_its_own_words_and_the_form_keeps_what_was_typed()
    {
        var page = await OpenSetupAsync();
        var form = page.GetByTestId("setup-form");
        var cases = new (string Username, string Password, string Reason)[]
        {
            ("owner1", "password1234", "Password is too common. Choose a stronger password."),
            ("owner1", "owner1-and-more-text", "Password must not contain the username."),
            ("owner1", "abababab1", "Password must use a wider mix of characters."),
        };

        foreach (var (username, password, reason) in cases)
        {
            await FillAsync(page, username, password, password);
            await page.GetByTestId("setup-submit").ClickAsync();

            await Expect(SetupWizard.Alert(form)).ToHaveTextAsync(reason, new() { Timeout = Navigation.SubmitSettleMs });
            await Expect(page.GetByTestId("setup-username")).ToHaveValueAsync(username);
            await Expect(page.GetByTestId("setup-password")).ToHaveValueAsync(password);
            await Expect(page.GetByTestId("setup-submit")).ToBeEnabledAsync();
            await Expect(page).ToHaveURLAsync($"{BaseUrl}/setup");
            Assert.True(await AccountCreationOpenAsync(page.Context), $"'{password}' created an account.");
        }

        await Axe.AssertNoViolationsAsync(page, "The first-run page with the server's reason shown");
    }

    [E2EFact]
    public async Task A_name_that_is_taken_is_refused_with_409_and_a_new_name_then_creates_the_account()
    {
        E2EDatabase.InsertViewerNamed(Server.DatabasePath, "taken");
        var page = await OpenSetupAsync();
        await FillAsync(page, "taken", StrongPassword, StrongPassword);

        var refused = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByTestId("setup-submit").ClickAsync(),
            response => response.Url.EndsWith(BootstrapPath, StringComparison.Ordinal),
            new() { Timeout = Navigation.SubmitSettleMs });

        Assert.Equal(409, refused.Status);
        await Expect(SetupWizard.Alert(page.GetByTestId("setup-form"))).ToHaveTextAsync("Username already exists.");
        Assert.True(await AccountCreationOpenAsync(page.Context));

        await page.GetByTestId("setup-username").FillAsync("not-taken");
        await Expect(SetupWizard.Alert(page)).ToHaveCountAsync(0);
        await page.GetByTestId("setup-submit").ClickAsync();
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
    }

    [E2EFact]
    public async Task Each_password_box_shows_and_hides_its_text_and_hides_again_once_it_is_emptied()
    {
        var page = await OpenSetupAsync();
        var password = page.GetByTestId("setup-password");
        var confirmation = page.GetByTestId("setup-confirm-password");
        await FillAsync(page, "owner", StrongPassword, StrongPassword);
        await Expect(password).ToHaveAttributeAsync("type", "password");

        await page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true }).ClickAsync();
        await Expect(password).ToHaveAttributeAsync("type", "text");
        await Expect(confirmation).ToHaveAttributeAsync("type", "password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide password", Exact = true }).ClickAsync();
        await Expect(password).ToHaveAttributeAsync("type", "password");

        await page.GetByRole(AriaRole.Button, new() { Name = "Show confirmation", Exact = true }).ClickAsync();
        await Expect(confirmation).ToHaveAttributeAsync("type", "text");
        await Expect(password).ToHaveAttributeAsync("type", "password");
        await confirmation.FillAsync(string.Empty);
        await Expect(confirmation).ToHaveAttributeAsync("type", "password");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Show confirmation", Exact = true })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task On_a_fresh_install_sign_in_leads_to_account_creation_and_the_two_pages_link_to_each_other()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/setup");

        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login");
        await Expect(page.GetByText("First-time setup?", new() { Exact = false })).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "Sign-in on an install with no account yet");

        await page.GetByRole(AriaRole.Link, new() { Name = "Create the admin account", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/setup");
    }

    [E2EFact]
    public async Task The_page_fits_a_phone_and_the_account_can_be_created_by_keyboard_alone()
    {
        var page = await OpenSetupAsync(new ViewportSize { Width = 375, Height = 812 });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The page scrolls sideways.");
        await Axe.AssertNoViolationsAsync(page, "The first-run page at phone width");

        await page.GetByTestId("setup-username").FocusAsync();
        await page.Keyboard.TypeAsync(Navigation.BootstrapUser);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(Navigation.BootstrapPassword);
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true })).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(Navigation.BootstrapPassword);
        await page.Keyboard.PressAsync("Enter");

        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
    }
}
