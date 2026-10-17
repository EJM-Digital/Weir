using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Security as the server describes it: the Sign-in card for this browser, the rows of Active sessions, the protections in
/// force, and the eye that shows a password being typed. Each is compared with what the server says, read through its API.
/// </summary>
public sealed class SecurityCardsTests(E2EServer server) : E2ETestBase(server)
{
    private static ILocator Fact(IPage page, string label) =>
        page.Locator("dl[aria-label='How this browser is signed in'] > div").Filter(new() { HasText = label });

    private static string Words(int minutes) => minutes switch
    {
        _ when minutes % 1440 == 0 => Count(minutes / 1440, "day"),
        _ when minutes % 60 == 0 => Count(minutes / 60, "hour"),
        _ => Count(minutes, "minute"),
    };

    private static string Count(int number, string unit) => number == 1 ? $"1 {unit}" : $"{number} {unit}s";

    private static async Task TrustedSignInAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("login-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("login-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Trust this device", Exact = true }).CheckAsync();
        await page.GetByTestId("login-submit").ClickAsync();
        await Expect(page.GetByTestId("shell-ready").Or(page.GetByTestId("setup-wizard-skip")).First)
            .ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
    }

    [E2EFact]
    public async Task The_sign_in_card_says_this_browser_is_standard_with_the_lifetimes_the_server_reports()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var me = await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/auth/session");
        var overview = await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/suite/security-overview");
        Assert.False(me.GetProperty("trusted_device").GetBoolean());

        await SecurityTab.OpenAsync(page);

        var browser = Fact(page, "This browser").Locator("dd");
        await Expect(browser).ToHaveTextAsync("Standard", new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(browser).ToHaveAttributeAsync("title", "Normal sign-in lifetime");
        var idle = Words(me.GetProperty("idle_timeout_minutes").GetInt32());
        var days = Count(me.GetProperty("absolute_timeout_days").GetInt32(), "day");
        await Expect(Fact(page, "Signs out after").Locator("dd")).ToHaveTextAsync($"{idle} idle · at most {days}");
        var trusted = Fact(page, "Trusted devices").Locator("dd");
        await Expect(trusted).ToHaveTextAsync(overview.GetProperty("trusted_session_absolute_timeout_plain").GetString()!);
        await Expect(trusted).ToHaveAttributeAsync("title", $"Idle timeout {overview.GetProperty("trusted_session_idle_timeout_plain").GetString()}");
    }

    [E2EFact]
    public async Task A_browser_signed_in_as_trusted_says_so_on_the_card_and_in_the_sessions_list_beside_a_standard_one()
    {
        var standard = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(standard, BaseUrl);
        var trusted = await NewPageAsync();
        await TrustedSignInAsync(trusted, BaseUrl);

        await SecurityTab.OpenAsync(trusted);

        var browser = Fact(trusted, "This browser").Locator("dd");
        await Expect(browser).ToHaveTextAsync("Trusted", new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(browser).ToHaveAttributeAsync("title", "Long-lived sign-in for this device");
        await Expect(SecurityTab.SessionRows(trusted)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });
        var own = SecurityTab.SessionRows(trusted).Filter(new() { HasText = "This browser" });
        var other = SecurityTab.SessionRows(trusted).Filter(new() { HasNotText = "This browser" });
        await Expect(own).ToContainTextAsync("Trusted");
        await Expect(other).Not.ToContainTextAsync("Trusted");
        await Expect(other.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Each_session_row_shows_the_browser_name_and_the_last_seen_and_expiry_times_the_server_holds()
    {
        using var other = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var zone = (await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/suite/settings")).GetProperty("app_timezone").GetString()!;
        var sessions = (await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/auth/sessions")).GetProperty("items").EnumerateArray().ToList();
        Assert.True(sessions.Count >= 2, "The test needs the browser and at least one other sign-in.");

        await SecurityTab.OpenAsync(page);

        await Expect(SecurityTab.SessionRows(page)).ToHaveCountAsync(sessions.Count, new() { Timeout = SecurityTab.ServerAnswerMs });
        for (var index = 0; index < sessions.Count; index++)
        {
            // The list is in the order the server gave it, so row N is session N.
            var session = sessions[index];
            var row = SecurityTab.SessionRows(page).Nth(index);
            await Expect(row.Locator("th")).ToContainTextAsync(session.GetProperty("client_label").GetString()!);
            if (session.GetProperty("current").GetBoolean())
            {
                await Expect(row.Locator("th")).ToContainTextAsync("This browser");
            }
            else
            {
                await Expect(row.Locator("th")).Not.ToContainTextAsync("This browser");
            }

            var seen = await AppDates.ShownAsync(page, session.GetProperty("last_seen_at").GetString()!, zone);
            var expires = await AppDates.ShownAsync(page, session.GetProperty("absolute_expires_at").GetString()!, zone);
            await Expect(row.Locator("td[data-col='lastSeen']")).ToHaveTextAsync(seen);
            await Expect(row.Locator("td[data-col='expires']")).ToHaveTextAsync(expires);
        }
    }

    [E2EFact]
    public async Task The_protections_list_puts_rows_that_need_attention_first_with_the_advice_and_counts_them()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var overview = await PageApi.GetAsync(page.Context, BaseUrl, "/api/v1/suite/security-overview");
        Assert.False(overview.GetProperty("extra_https_hardening_enabled").GetBoolean());

        await SecurityTab.OpenAsync(page);

        var posture = page.GetByTestId("suite-security-posture");
        var rows = posture.Locator("li.mm-protection");
        await Expect(rows).ToHaveCountAsync(9, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(posture).ToContainTextAsync("9 protections — 1 row needs attention");
        await Expect(rows.First).ToHaveAttributeAsync("data-status", "attention");
        await Expect(rows.First).ToHaveTextAsync("Extra HTTPS protectionOff — review HTTPS deployment");
        await Expect(rows.Nth(1)).ToHaveAttributeAsync("data-status", "done");
        var advice = posture.Locator(".mm-protection__attention .mm-protection__advice");
        await Expect(advice).ToHaveTextAsync(overview.GetProperty("restart_required_note").GetString()!);
        await Expect(posture.Locator(".mm-sys-note")).ToHaveCountAsync(0);
        await Expect(rows.Filter(new() { HasText = "Sign-in security key" })).ToContainTextAsync("On");
    }

    [E2EFact]
    public async Task With_every_protection_on_the_list_says_so_and_the_note_moves_below_it()
    {
        await using var own = await E2EServer.StartWithSettingsAsync(("WEIR_SECURITY_ENABLE_HSTS", "1"));
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        var overview = await PageApi.GetAsync(page.Context, baseUrl, "/api/v1/suite/security-overview");

        await SecurityTab.OpenAsync(page);

        var posture = page.GetByTestId("suite-security-posture");
        await Expect(posture).ToContainTextAsync("9 protections in force", new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(posture.Locator("li.mm-protection[data-status='attention']")).ToHaveCountAsync(0);
        await Expect(posture.Locator(".mm-protection__attention")).ToHaveCountAsync(0);
        await Expect(posture.Locator(".mm-sys-note")).ToHaveTextAsync(overview.GetProperty("restart_required_note").GetString()!);
        await Expect(posture.Locator("li.mm-protection").Filter(new() { HasText = "Extra HTTPS protection" })).ToContainTextAsync("On");
    }

    [E2EFact]
    public async Task Every_password_field_has_an_eye_of_its_own_that_hides_again_when_the_field_is_emptied()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await SecurityTab.OpenAsync(page);

        (ILocator Form, string Placeholder, string Reveals)[] fields =
        [
            (SecurityTab.PasswordForm(page), "Enter current password", "password"),
            (SecurityTab.PasswordForm(page), "Enter new password", "new password"),
            (SecurityTab.PasswordForm(page), "Re-enter new password", "password confirmation"),
            (SecurityTab.UsernameForm(page), "Confirm it is you", "current password"),
        ];
        foreach (var (form, placeholder, reveals) in fields)
        {
            var input = form.GetByPlaceholder(placeholder, new() { Exact = true });
            await input.FillAsync("typed-secret-1");
            await Expect(input).ToHaveAttributeAsync("type", "password");

            await form.GetByRole(AriaRole.Button, new() { Name = $"Show {reveals}", Exact = true }).ClickAsync();
            await Expect(input).ToHaveAttributeAsync("type", "text");
            await form.GetByRole(AriaRole.Button, new() { Name = $"Hide {reveals}", Exact = true }).ClickAsync();
            await Expect(input).ToHaveAttributeAsync("type", "password");

            await form.GetByRole(AriaRole.Button, new() { Name = $"Show {reveals}", Exact = true }).ClickAsync();
            await input.FillAsync(string.Empty);
            await Expect(input).ToHaveAttributeAsync("type", "password");
            await Expect(form.GetByRole(AriaRole.Button, new() { Name = $"Show {reveals}", Exact = true })).ToBeVisibleAsync();
        }

        var current = SecurityTab.PasswordForm(page).GetByPlaceholder("Enter current password", new() { Exact = true });
        var next = SecurityTab.PasswordForm(page).GetByPlaceholder("Enter new password", new() { Exact = true });
        await current.FillAsync("typed-secret-1");
        await next.FillAsync("typed-secret-2");
        await SecurityTab.PasswordForm(page).GetByRole(AriaRole.Button, new() { Name = "Show password", Exact = true }).ClickAsync();
        await Expect(current).ToHaveAttributeAsync("type", "text");
        await Expect(next).ToHaveAttributeAsync("type", "password");
    }

    [E2EFact]
    public async Task A_password_that_was_shown_is_hidden_again_once_the_server_has_answered_the_form()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await SecurityTab.OpenAsync(page);

        var form = SecurityTab.PasswordForm(page);
        await form.GetByPlaceholder("Enter current password", new() { Exact = true }).FillAsync("not-the-current-password");
        await form.GetByPlaceholder("Enter new password", new() { Exact = true }).FillAsync("e2e-replacement-pass-9");
        await form.GetByPlaceholder("Re-enter new password", new() { Exact = true }).FillAsync("e2e-replacement-pass-9");
        await form.GetByRole(AriaRole.Button, new() { Name = "Show new password", Exact = true }).ClickAsync();
        await Expect(form.GetByPlaceholder("Enter new password", new() { Exact = true })).ToHaveAttributeAsync("type", "text");

        await form.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();

        await Expect(form.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Current password is incorrect.", new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(form.GetByPlaceholder("Enter new password", new() { Exact = true })).ToHaveAttributeAsync("type", "password");
        await Expect(form.GetByRole(AriaRole.Button, new() { Name = "Show new password", Exact = true })).ToBeVisibleAsync();

        var username = SecurityTab.UsernameForm(page);
        await username.GetByLabel("New username", new() { Exact = true }).FillAsync("someone-else");
        await username.GetByPlaceholder("Confirm it is you", new() { Exact = true }).FillAsync("not-the-current-password");
        await username.GetByRole(AriaRole.Button, new() { Name = "Show current password", Exact = true }).ClickAsync();
        await username.GetByRole(AriaRole.Button, new() { Name = "Change username", Exact = true }).ClickAsync();
        await Expect(username.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(username.GetByPlaceholder("Confirm it is you", new() { Exact = true })).ToHaveAttributeAsync("type", "password");
    }
}
