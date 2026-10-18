using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The light and dark switch in every page's header: it changes the page at once, remembers the choice in this browser, saves it to the account so
/// another browser follows it, and says so when the account could not be told.
/// </summary>
public sealed class ThemeToggleTests(E2EServer server) : E2ETestBase(server)
{
    private const string ThemePath = "/api/v1/auth/theme";
    private const string StorageKey = "weir-app-theme";

    private async Task<(IPage Page, RequestLog Log)> OpenAsync(ColorScheme system)
    {
        var page = await NewPageAsync(colorScheme: system);
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        return (page, RequestLog.Watch(page));
    }

    private static ILocator Toggle(IPage page) => page.GetByTestId("theme-toggle");

    private static Task ExpectThemeAsync(IPage page, string theme) =>
        Expect(page.Locator("html")).ToHaveAttributeAsync("data-mm-theme", theme);

    private static async Task<string?> AccountThemeAsync(IBrowserContext context, string baseUrl)
    {
        await using var response = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/auth/me");
        Assert.True(response.Ok, $"Asking who is signed in answered {response.Status}.");
        // An account that never chose has no theme to report: the field is null or left out.
        var me = JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("user");
        return me.TryGetProperty("app_theme", out var theme) && theme.ValueKind == JsonValueKind.String ? theme.GetString() : null;
    }

    [E2EFact]
    public async Task Pressing_it_changes_the_page_at_once_remembers_it_here_and_saves_it_to_the_account()
    {
        var (page, log) = await OpenAsync(ColorScheme.Light);
        await ExpectThemeAsync(page, "light");
        await Expect(Toggle(page)).ToHaveAccessibleNameAsync("Switch to dark mode");
        await Expect(Toggle(page)).ToHaveAttributeAsync("title", "Switch to dark mode");
        // The save is held back, to show the page does not wait for it.
        var release = new TaskCompletionSource();
        await page.RouteAsync($"**{ThemePath}", async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        var mark = log.Mark();

        await Toggle(page).ClickAsync();

        await ExpectThemeAsync(page, "dark");
        await Expect(Toggle(page)).ToHaveAccessibleNameAsync("Switch to light mode");
        Assert.Equal("dark", await page.EvaluateAsync<string>($"() => localStorage.getItem('{StorageKey}')"));
        release.SetResult();
        await Poll.UntilAsync(async () => await AccountThemeAsync(page.Context, BaseUrl) == "dark", "the account to hold the dark theme");
        var sent = Assert.Single(log.Since(mark));
        Assert.Equal("POST", sent.Method);
        Assert.Equal(ThemePath, sent.PathAndQuery);
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.Equal("dark", (string?)body["theme"]);
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The save carries the page's CSRF token.");
        await Expect(page.GetByTestId("theme-toggle-alert")).ToHaveCountAsync(0);
        await Axe.AssertNoViolationsAsync(page, "The app in the dark theme");

        // The choice outlasts the page, and another browser that signs in as the same person follows the account.
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await ExpectThemeAsync(page, "dark");
        var other = await NewPageAsync(colorScheme: ColorScheme.Light);
        await Navigation.EnsureSignedInAsync(other, BaseUrl);
        await ExpectThemeAsync(other, "dark");

        await Toggle(page).ClickAsync();
        await ExpectThemeAsync(page, "light");
        await Poll.UntilAsync(async () => await AccountThemeAsync(page.Context, BaseUrl) == "light", "the account to hold the light theme");
        await Axe.AssertNoViolationsAsync(page, "The app in the light theme");
    }

    [E2EFact]
    public async Task It_works_from_the_keyboard_and_a_phone()
    {
        var page = await NewPageAsync(new ViewportSize { Width = 390, Height = 844 }, colorScheme: ColorScheme.Dark);
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await ExpectThemeAsync(page, "dark");
        await Toggle(page).FocusAsync();

        await page.Keyboard.PressAsync("Enter");

        await ExpectThemeAsync(page, "light");
        await Poll.UntilAsync(async () => await AccountThemeAsync(page.Context, BaseUrl) == "light", "the account to hold the light theme");
        await page.Keyboard.PressAsync("Space");
        await ExpectThemeAsync(page, "dark");
        await Expect(Toggle(page)).ToBeFocusedAsync();
        await Poll.UntilAsync(async () => await AccountThemeAsync(page.Context, BaseUrl) == "dark", "the account to hold the dark theme");
    }

    [E2EFact]
    public async Task A_save_the_server_refuses_says_other_browsers_were_not_told_and_keeps_the_choice_here()
    {
        var (page, _) = await OpenAsync(ColorScheme.Light);
        var before = await AccountThemeAsync(page.Context, BaseUrl);
        await page.RouteAsync($"**{ThemePath}", async route =>
        {
            // The server's own refusal: a token it does not know.
            var body = JsonNode.Parse(route.Request.PostData!)!.AsObject();
            body["csrf_token"] = "not-the-token";
            await route.ContinueAsync(new() { PostData = Encoding.UTF8.GetBytes(body.ToJsonString()) });
        });

        await Toggle(page).ClickAsync();

        await Expect(page.GetByTestId("theme-toggle-alert")).ToHaveTextAsync("Weir couldn't save your theme for other browsers. It's changed here for now.");
        await Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Weir couldn't save your theme" })).ToBeVisibleAsync();
        await ExpectThemeAsync(page, "dark");
        Assert.Equal("dark", await page.EvaluateAsync<string>($"() => localStorage.getItem('{StorageKey}')"));
        Assert.Equal(before, await AccountThemeAsync(page.Context, BaseUrl));
        await Axe.AssertNoViolationsAsync(page, "The header after a theme the account could not save");
    }
}
