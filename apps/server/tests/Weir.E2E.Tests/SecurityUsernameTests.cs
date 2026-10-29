using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Security › Change username against the real server: what is refused, in which words, and what a rename does.</summary>
public sealed class SecurityUsernameTests(E2EServer server) : E2ETestBase(server)
{
    private const string NewName = "e2e-renamed-admin";
    private const string ChangeEndpoint = "/api/v1/auth/change-username";
    private const string UsernameChanged = "auth.username_changed";

    [E2EFact]
    public async Task Renaming_the_account_trims_the_name_clears_the_form_and_changes_who_is_shown_signed_in()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var other = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(other, BaseUrl);
        await SecurityTab.OpenAsync(page);
        var form = SecurityTab.UsernameForm(page);
        await Expect(page.GetByTestId("user-menu")).ToContainTextAsync(Navigation.BootstrapUser);

        await Fill(page, $"  {NewName}  ", Navigation.BootstrapPassword);
        var change = await page.RunAndWaitForRequestAsync(
            async () => await form.GetByRole(AriaRole.Button, new() { Name = "Change username", Exact = true }).ClickAsync(),
            request => request.Method == "POST" && request.Url.EndsWith(ChangeEndpoint, StringComparison.Ordinal));

        Assert.Equal(NewName, System.Text.Json.JsonDocument.Parse(change.PostData!).RootElement.GetProperty("new_username").GetString());
        await Expect(form.GetByRole(AriaRole.Status)).ToHaveTextAsync("Username changed. Use it the next time you sign in.", new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(form.GetByPlaceholder("Enter a new username")).ToHaveValueAsync(string.Empty);
        await Expect(form.GetByPlaceholder("Confirm it is you")).ToHaveValueAsync(string.Empty);
        await Expect(page.GetByTestId("user-menu")).ToContainTextAsync(NewName, new() { Timeout = SecurityTab.ServerAnswerMs });
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(page.Context, BaseUrl));
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(other.Context, BaseUrl));

        var fresh = await NewPageAsync();
        await SecurityTab.SubmitSignInAsync(fresh, BaseUrl, Navigation.BootstrapUser, Navigation.BootstrapPassword);
        await Expect(fresh.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await SecurityTab.SignInAsync(fresh, BaseUrl, NewName, Navigation.BootstrapPassword);
    }

    [E2EFact]
    public async Task Renaming_the_account_is_recorded_in_the_log()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var recordedBefore = await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, UsernameChanged);
        await SecurityTab.OpenAsync(page);
        await Fill(page, NewName, Navigation.BootstrapPassword);

        await SecurityTab.UsernameForm(page).GetByRole(AriaRole.Button, new() { Name = "Change username", Exact = true }).ClickAsync();
        await Expect(SecurityTab.UsernameForm(page).GetByRole(AriaRole.Status)).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });

        Assert.Equal(recordedBefore + 1, await SecurityTab.RecordedEventCountAsync(page.Context, BaseUrl, UsernameChanged));
        await Navigation.OpenLogsAsync(page, "Events");
        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Username changed" }).First).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
    }

    [E2EFact]
    public async Task Missing_fields_are_asked_for_before_anything_is_sent()
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
        var form = SecurityTab.UsernameForm(page);

        await Fill(page, "   ", Navigation.BootstrapPassword);
        await form.GetByRole(AriaRole.Button, new() { Name = "Change username", Exact = true }).ClickAsync();

        await Expect(form.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Enter the new username and your current password.");
        Assert.Empty(sent);
    }

    [E2EFact]
    public async Task Each_refusal_from_the_server_is_said_in_plain_words_and_the_account_keeps_its_name()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await SecurityTab.OpenAsync(page);
        var form = SecurityTab.UsernameForm(page);
        var submit = form.GetByRole(AriaRole.Button, new() { Name = "Change username", Exact = true });

        (string Name, string Password, string Said)[] refusals =
        [
            (NewName, "not-the-current-password", "Current password is not correct."),
            (Navigation.BootstrapUser, Navigation.BootstrapPassword, "New username must be different from the current username."),
        ];
        foreach (var (name, password, said) in refusals)
        {
            await Fill(page, name, password);
            await submit.ClickAsync();

            await Expect(form.GetByRole(AriaRole.Alert)).ToHaveTextAsync(said, new() { Timeout = SecurityTab.ServerAnswerMs });
            await Expect(submit).ToBeEnabledAsync();
            await Expect(form.GetByPlaceholder("Enter a new username")).ToBeEditableAsync();
            await Expect(form.GetByText("Username changed.")).ToHaveCountAsync(0);
        }

        await Expect(page.GetByTestId("user-menu")).ToContainTextAsync(Navigation.BootstrapUser);
        var fresh = await NewPageAsync();
        await SecurityTab.SignInAsync(fresh, BaseUrl, Navigation.BootstrapUser, Navigation.BootstrapPassword);
    }

    private static async Task Fill(IPage page, string name, string password)
    {
        var form = SecurityTab.UsernameForm(page);
        await form.GetByPlaceholder("Enter a new username").FillAsync(name);
        await form.GetByPlaceholder("Confirm it is you").FillAsync(password);
    }
}
