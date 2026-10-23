using System.Text.Json;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The sign-in page against the real server: what signing in sends and leaves behind, how it is done by keyboard, trusting the device,
/// who is refused and in which words, the limit on attempts, and the notices the page can open with. A wrong password and a session
/// that ended are also covered by <see cref="SignInRefusalTests"/> and <see cref="FirstVisitTests"/>.
/// </summary>
public sealed class SignInTests(E2EServer server) : E2ETestBase(server)
{
    private const string LoginPath = "/api/v1/auth/login";
    private const string LoginSucceeded = "auth.login_succeeded";

    /// <summary>An install whose administrator finished (skipped) setup, so a sign-in lands on the Dashboard.</summary>
    private async Task<WeirClient> ArrangeAccountAsync()
    {
        var admin = new WeirClient(new Uri(BaseUrl));
        try
        {
            await admin.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            await SetupState.SkipWizardAsync(admin);
            return admin;
        }
        catch
        {
            admin.Dispose();
            throw;
        }
    }

    /// <summary>How many successful sign-ins Weir has recorded, read by the administrator the test arranged.</summary>
    private static async Task<int> SignInsRecordedAsync(WeirClient admin)
    {
        var recent = await admin.GetAsync($"{WeirClient.Api}/activity/recent", ("event_type", LoginSucceeded), ("limit", 1));
        return recent.Fields["total"]!.GetValue<int>();
    }

    private async Task<IPage> OpenSignInAsync(ViewportSize? viewport = null)
    {
        var page = await NewPageAsync(viewport);
        await page.GotoAsync($"{BaseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        return page;
    }

    private static JsonElement BodyOf(IRequest request) => JsonDocument.Parse(request.PostData!).RootElement;

    private static async Task<JsonElement> CurrentSessionAsync(IBrowserContext context, string baseUrl)
    {
        await using var response = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/auth/session");
        Assert.True(response.Ok, $"Reading the session answered {response.Status}.");
        return JsonDocument.Parse(await response.TextAsync()).RootElement;
    }

    [E2EFact]
    public async Task Signing_in_sends_the_trimmed_name_lands_on_the_Dashboard_signed_in_and_never_returns_to_the_form()
    {
        using var admin = await ArrangeAccountAsync();
        var page = await OpenSignInAsync();
        await Expect(page.GetByTestId("login-username")).ToBeFocusedAsync();
        await Expect(page.GetByText("First-time setup?", new() { Exact = false })).ToHaveCountAsync(0);
        await Axe.AssertNoViolationsAsync(page, "The sign-in page");
        var navigations = new List<string>();
        var refusedWhoAmI = new List<string>();
        page.FrameNavigated += (_, frame) => navigations.Add(frame.Url);
        page.Response += (_, response) =>
        {
            if (response.Status == 401 && response.Url.EndsWith("/api/v1/auth/me", StringComparison.Ordinal))
            {
                refusedWhoAmI.Add(response.Url);
            }
        };
        var recordedBefore = await SignInsRecordedAsync(admin);
        await page.GetByTestId("login-username").FillAsync($"  {Navigation.BootstrapUser}  ");
        await page.GetByTestId("login-password").FillAsync(Navigation.BootstrapPassword);
        refusedWhoAmI.Clear();
        navigations.Clear();

        var signIn = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByTestId("login-password").PressAsync("Enter"),
            request => request.Method == "POST" && request.Url.EndsWith(LoginPath, StringComparison.Ordinal));

        var sent = BodyOf(signIn);
        Assert.Equal(Navigation.BootstrapUser, sent.GetProperty("username").GetString());
        Assert.Equal(Navigation.BootstrapPassword, sent.GetProperty("password").GetString());
        Assert.False(sent.GetProperty("trusted_device").GetBoolean());
        Assert.False(string.IsNullOrEmpty(sent.GetProperty("csrf_token").GetString()));
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/");
        await Expect(page.GetByTestId("user-menu")).ToContainTextAsync(Navigation.BootstrapUser);
        Assert.DoesNotContain(navigations, url => url.Contains("/login", StringComparison.Ordinal));
        Assert.Empty(refusedWhoAmI);
        Assert.Equal("1", await page.EvaluateAsync<string>("() => window.localStorage.getItem('mm:signed-in-before')"));
        Assert.False((await CurrentSessionAsync(page.Context, BaseUrl)).GetProperty("trusted_device").GetBoolean());
        Assert.Equal(recordedBefore + 1, await SignInsRecordedAsync(admin));
    }

    [E2EFact]
    public async Task Trusting_the_device_is_reached_and_chosen_by_keyboard_alone_and_the_session_is_kept_as_trusted()
    {
        using var _ = await ArrangeAccountAsync();
        var page = await OpenSignInAsync();
        var trust = page.GetByRole(AriaRole.Checkbox, new() { Name = "Trust this device", Exact = true });
        await Expect(trust).Not.ToBeCheckedAsync();

        await page.Keyboard.TypeAsync(Navigation.BootstrapUser);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(Navigation.BootstrapPassword);
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true })).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(trust).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Space");
        await Expect(trust).ToBeCheckedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByTestId("login-submit")).ToBeFocusedAsync();
        var signIn = await page.RunAndWaitForRequestAsync(
            async () => await page.Keyboard.PressAsync("Enter"),
            request => request.Method == "POST" && request.Url.EndsWith(LoginPath, StringComparison.Ordinal));

        Assert.True(BodyOf(signIn).GetProperty("trusted_device").GetBoolean());
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.True((await CurrentSessionAsync(page.Context, BaseUrl)).GetProperty("trusted_device").GetBoolean());
    }

    [E2EFact]
    public async Task A_name_Weir_does_not_know_is_refused_in_the_same_words_as_a_wrong_password_and_nothing_is_signed_in()
    {
        using var _ = await ArrangeAccountAsync();
        var page = await OpenSignInAsync();
        await page.GetByTestId("login-username").FillAsync("nobody-here");
        await page.GetByTestId("login-password").FillAsync(Navigation.BootstrapPassword);

        var refused = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByTestId("login-submit").ClickAsync(),
            response => response.Url.EndsWith(LoginPath, StringComparison.Ordinal),
            new() { Timeout = Navigation.SubmitSettleMs });

        Assert.Equal(401, refused.Status);
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Invalid username or password.");
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/login");
        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));
        await Axe.AssertNoViolationsAsync(page, "The sign-in page with a refusal shown");
    }

    [E2EFact]
    public async Task Too_many_attempts_from_one_address_are_refused_in_plain_words_with_the_wait_and_the_form_stays_usable()
    {
        await using var weir = await E2EServer.StartWithSettingsAsync(
            ("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "3"),
            ("WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS", "600"));
        var baseUrl = weir.BaseUrl.GetLeftPart(UriPartial.Authority);
        using (var admin = new WeirClient(weir.BaseUrl))
        {
            var created = await admin.BootstrapAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            Assert.True(created.Status == System.Net.HttpStatusCode.OK, created.ToString());
        }

        var page = await NewPageAsync();
        await page.GotoAsync($"{baseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("login-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("login-password").FillAsync("not-the-password");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var answer = await SubmitAsync(page, baseUrl);
            Assert.Equal(401, answer.Status);
            await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Invalid username or password.");
        }

        var limited = await SubmitAsync(page, baseUrl);

        Assert.Equal(429, limited.Status);
        Assert.True(int.Parse(limited.Headers["retry-after"], System.Globalization.CultureInfo.InvariantCulture) > 0, "The refusal did not say how long to wait.");
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Too many attempts. Wait a few minutes, then try again.");
        await Expect(page.GetByTestId("login-submit")).ToBeEnabledAsync();
        await Expect(page.GetByTestId("login-username")).ToHaveValueAsync(Navigation.BootstrapUser);

        await page.GetByTestId("login-password").FillAsync(Navigation.BootstrapPassword);
        var stillLimited = await SubmitAsync(page, baseUrl);
        Assert.Equal(429, stillLimited.Status);
        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(page.Context, baseUrl));
        await Axe.AssertNoViolationsAsync(page, "The sign-in page with the limit shown");
    }

    private static async Task<IResponse> SubmitAsync(IPage page, string baseUrl) =>
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByTestId("login-submit").ClickAsync(),
            response => response.Url == $"{baseUrl}{LoginPath}",
            new() { Timeout = Navigation.SubmitSettleMs });

    [E2EFact]
    public async Task The_page_says_why_the_person_is_there_when_it_is_opened_with_a_reason()
    {
        using var _ = await ArrangeAccountAsync();
        var page = await NewPageAsync();
        var cases = new (string Query, AriaRole Role, string Text)[]
        {
            ("?password=changed", AriaRole.Status, "Password changed. Sign in with your new password."),
            ("?session=expired", AriaRole.Status, "Your session expired. Sign in again to keep using Weir."),
            ("?bootstrap=created", AriaRole.Status, "Initial account created. Sign in with the credentials you chose."),
            ("?problem=session-not-kept", AriaRole.Alert, "Your password was correct, but the session did not stick."),
        };

        foreach (var (query, role, text) in cases)
        {
            await page.GotoAsync($"{BaseUrl}/login{query}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            await Expect(page.GetByRole(role).Filter(new() { HasText = text })).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
            await Axe.AssertNoViolationsAsync(page, $"The sign-in page opened with {query}");
        }

        await Expect(page.GetByText("WEIR_SESSION_COOKIE_SECURE=auto")).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Someone_already_signed_in_who_opens_the_sign_in_address_is_taken_to_the_Dashboard()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await page.GotoAsync($"{BaseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/");
        await Expect(page.GetByTestId("login-username")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task The_page_fits_a_phone_and_the_password_box_shows_and_hides_its_text()
    {
        using var _ = await ArrangeAccountAsync();
        var page = await OpenSignInAsync(new ViewportSize { Width = 375, Height = 812 });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The page scrolls sideways.");
        await Axe.AssertNoViolationsAsync(page, "The sign-in page at phone width");
        var password = page.GetByTestId("login-password");
        await password.FillAsync(Navigation.BootstrapPassword);
        await Expect(password).ToHaveAttributeAsync("type", "password");

        await page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true }).ClickAsync();
        await Expect(password).ToHaveAttributeAsync("type", "text");
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide password", Exact = true }).ClickAsync();
        await Expect(password).ToHaveAttributeAsync("type", "password");
        await password.FillAsync(string.Empty);
        await page.GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true }).ClickAsync();
        await password.FillAsync("x");
        await Expect(password).ToHaveAttributeAsync("type", "text");
        await password.FillAsync(string.Empty);
        await Expect(password).ToHaveAttributeAsync("type", "password");
    }
}
