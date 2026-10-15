using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Security › Change password against the real server: what is refused, in which words, and what a change does.</summary>
public sealed partial class SecurityPasswordTests(E2EServer server) : E2ETestBase(server)
{
    private const string NewPassword = "e2e-replacement-pass-9";
    private const string ChangeEndpoint = "/api/v1/auth/change-password";

    [E2EFact]
    public async Task New_passwords_that_differ_are_refused_before_anything_is_sent()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var sent = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith(ChangeEndpoint, StringComparison.Ordinal))
            {
                sent.Add(request.Method);
            }
        };
        await SecurityTab.OpenAsync(page);

        await FillAsync(page, Navigation.BootstrapPassword, NewPassword, NewPassword + "x");
        await SecurityTab.PasswordForm(page).GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();

        await Expect(SecurityTab.PasswordForm(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync("New passwords do not match.");
        Assert.Empty(sent);
        await AssertStillTheOldPasswordAsync(NewPassword);
    }

    [E2EFact]
    public async Task Missing_password_fields_are_asked_for_before_anything_is_sent()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var sent = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith(ChangeEndpoint, StringComparison.Ordinal))
            {
                sent.Add(request.Method);
            }
        };
        await SecurityTab.OpenAsync(page);

        await FillAsync(page, Navigation.BootstrapPassword, NewPassword, string.Empty);
        await SecurityTab.PasswordForm(page).GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();

        await Expect(SecurityTab.PasswordForm(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync("Fill in all three password fields.");
        Assert.Empty(sent);
    }

    [E2EFact]
    public async Task Each_refusal_from_the_server_is_said_in_plain_words_and_the_form_stays_editable()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await SecurityTab.OpenAsync(page);
        var form = SecurityTab.PasswordForm(page);
        var submit = form.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true });

        (string Current, string Next, string Said)[] refusals =
        [
            ("not-the-current-password", NewPassword, "Current password is incorrect."),
            (Navigation.BootstrapPassword, Navigation.BootstrapPassword, "New password must be different from the current password."),
            (Navigation.BootstrapPassword, "short1!", "New password must be at least 8 characters."),
            (Navigation.BootstrapPassword, "password123", "Password is too common. Choose a stronger password."),
            (Navigation.BootstrapPassword, $"{Navigation.BootstrapUser}-x1", "Password must not contain the username."),
            (Navigation.BootstrapPassword, "aaaaaaaa", "Password must use a wider mix of characters."),
        ];
        foreach (var (current, next, said) in refusals)
        {
            await FillAsync(page, current, next, next);
            await submit.ClickAsync();

            await Expect(form.GetByRole(AriaRole.Alert)).ToHaveTextAsync(said, new() { Timeout = SecurityTab.ServerAnswerMs });
            await Expect(submit).ToBeEnabledAsync();
            await Expect(form.GetByPlaceholder("Enter new password", new() { Exact = true })).ToBeEditableAsync();
            await Expect(form.GetByPlaceholder("Enter new password", new() { Exact = true })).ToHaveValueAsync(next);
            await Expect(form.GetByText("Password changed.")).ToHaveCountAsync(0);
        }

        await AssertStillTheOldPasswordAsync(NewPassword);
    }

    [E2EFact]
    public async Task Changing_the_password_ends_every_sign_in_and_only_the_new_password_signs_in_again()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var other = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(other, BaseUrl);
        await SecurityTab.OpenAsync(page);
        var form = SecurityTab.PasswordForm(page);

        await FillAsync(page, Navigation.BootstrapPassword, NewPassword, NewPassword);
        var change = await page.RunAndWaitForRequestAsync(
            async () => await form.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync(),
            request => request.Method == "POST" && request.Url.EndsWith(ChangeEndpoint, StringComparison.Ordinal));
        Assert.Equal(200, (await change.ResponseAsync())!.Status);

        await Expect(page.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(page).ToHaveURLAsync(PasswordChangedUrl(), new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(page.GetByText("Password changed. Sign in with your new password.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("Your session expired.", new() { Exact = false })).ToHaveCountAsync(0);
        await Expect(page.GetByText("the session did not stick", new() { Exact = false })).ToHaveCountAsync(0);
        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(other.Context, BaseUrl));
        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));

        var again = await NewPageAsync();
        await SecurityTab.SubmitSignInAsync(again, BaseUrl, Navigation.BootstrapUser, Navigation.BootstrapPassword);
        await Expect(again.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await SecurityTab.SignInAsync(again, BaseUrl, Navigation.BootstrapUser, NewPassword);
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(again.Context, BaseUrl));
    }

    [GeneratedRegex(@".*/login\?password=changed$")]
    private static partial Regex PasswordChangedUrl();

    private static async Task FillAsync(IPage page, string current, string next, string confirm)
    {
        var form = SecurityTab.PasswordForm(page);
        await form.GetByPlaceholder("Enter current password", new() { Exact = true }).FillAsync(current);
        await form.GetByPlaceholder("Enter new password", new() { Exact = true }).FillAsync(next);
        await form.GetByPlaceholder("Re-enter new password", new() { Exact = true }).FillAsync(confirm);
    }

    /// <summary>Signs in from a browser of its own: the old password still works and the one that was refused does not.</summary>
    private async Task AssertStillTheOldPasswordAsync(string refused)
    {
        var fresh = await NewPageAsync();
        await SecurityTab.SubmitSignInAsync(fresh, BaseUrl, Navigation.BootstrapUser, refused);
        await Expect(fresh.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await SecurityTab.SignInAsync(fresh, BaseUrl, Navigation.BootstrapUser, Navigation.BootstrapPassword);
    }
}
