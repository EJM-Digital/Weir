using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>System › Security as a person uses it: the sessions list, the account forms, and signing in again from another browser.</summary>
public static class SecurityTab
{
    public const string SessionsHeading = "Active sessions";
    public const string SignOutOthers = "Sign out other sessions";

    /// <summary>The longest a request the server answers (it hashes passwords, and several tests share the machine) may take before a test gives up on it.</summary>
    public const float ServerAnswerMs = 60_000;

    public static async Task OpenAsync(IPage page)
    {
        await Navigation.OpenTabAsync(page, "System", "Security");
        await Expect(SessionsTable(page)).ToBeVisibleAsync();
    }

    public static ILocator SessionsTable(IPage page) => page.GetByTestId("active-sessions");

    public static ILocator SessionRows(IPage page) => SessionsTable(page).Locator("tbody tr");

    public static ILocator SignOutOthersButton(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = SignOutOthers, Exact = true });

    public static ILocator Account(IPage page) => page.GetByTestId("suite-security-account");

    /// <summary>The Account card's Change password form.</summary>
    public static ILocator PasswordForm(IPage page) => Account(page).GetByRole(AriaRole.Region, new() { Name = "Change password" });

    /// <summary>The Account card's Change username form.</summary>
    public static ILocator UsernameForm(IPage page) => Account(page).GetByRole(AriaRole.Region, new() { Name = "Change username" });

    /// <summary>The HTTP status the server gives the context's browser when it asks who is signed in: 200 while its session lives, 401 once it has ended.</summary>
    public static async Task<int> WhoAmIStatusAsync(IBrowserContext context, string baseUrl)
    {
        await using var response = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/auth/me");
        return response.Status;
    }

    /// <summary>How many events of this type the server has recorded, read as the signed-in person in the context.</summary>
    public static async Task<int> RecordedEventCountAsync(IBrowserContext context, string baseUrl, string eventType)
    {
        await using var response = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/activity/recent?event_type={eventType}&limit=100");
        Assert.True(response.Ok, $"Reading the recorded {eventType} events answered {response.Status}.");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("items").GetArrayLength();
    }

    /// <summary>Opens the sign-in page and submits these credentials; the caller says what should follow.</summary>
    public static async Task SubmitSignInAsync(IPage page, string baseUrl, string username, string password)
    {
        await page.GotoAsync($"{baseUrl.TrimEnd('/')}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("login-username").FillAsync(username);
        await page.GetByTestId("login-password").FillAsync(password);
        await page.GetByTestId("login-submit").ClickAsync();
    }

    /// <summary>Signs in with these credentials and waits for the shell.</summary>
    public static async Task SignInAsync(IPage page, string baseUrl, string username, string password)
    {
        await SubmitSignInAsync(page, baseUrl, username, password);
        await Expect(page.GetByTestId("shell-ready").Or(page.GetByTestId("setup-wizard-skip")).First)
            .ToBeVisibleAsync(new() { Timeout = ServerAnswerMs });
    }
}
