using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Security › Active sessions against the real server, with a second and third browser signed in as the same person.</summary>
public sealed partial class SecuritySessionsTests(E2EServer server) : E2ETestBase(server)
{
    private const string SessionsRevoked = "auth.sessions_revoked";

    [E2EFact]
    public async Task A_lone_session_is_listed_as_this_browser_with_nothing_to_sign_out()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        await SecurityTab.OpenAsync(page);

        await Expect(SecurityTab.SessionRows(page)).ToHaveCountAsync(1, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(page).First).ToContainTextAsync("This browser");
        await Expect(SecurityTab.SessionsTable(page).GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToHaveCountAsync(0);
        await Expect(SecurityTab.SignOutOthersButton(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Signing_out_other_sessions_ends_the_other_browser_and_keeps_this_one()
    {
        var first = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(first, BaseUrl);
        var second = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(second, BaseUrl);
        await SecurityTab.OpenAsync(first);
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first).Filter(new() { HasText = "This browser" })).ToHaveCountAsync(1);

        var revoke = await first.RunAndWaitForRequestAsync(
            async () => await SecurityTab.SignOutOthersButton(first).ClickAsync(),
            request => request.Method == "POST" && request.Url.EndsWith("/api/v1/auth/sessions/revoke-others", StringComparison.Ordinal));
        Assert.Equal(200, (await revoke.ResponseAsync())!.Status);

        await Expect(first.GetByRole(AriaRole.Status).Filter(new() { HasText = "Signed out 1 other session." })).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(1, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first).First).ToContainTextAsync("This browser");
        await Expect(SecurityTab.SignOutOthersButton(first)).ToHaveCountAsync(0, new() { Timeout = SecurityTab.ServerAnswerMs });

        Assert.Equal(401, await SecurityTab.WhoAmIStatusAsync(second.Context, BaseUrl));
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(first.Context, BaseUrl));
        await first.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(first.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
    }

    [E2EFact]
    public async Task The_browser_that_was_signed_out_is_sent_to_the_sign_in_page_on_its_next_action()
    {
        var first = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(first, BaseUrl);
        var second = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(second, BaseUrl);
        await SecurityTab.OpenAsync(first);
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });

        await SecurityTab.SignOutOthersButton(first).ClickAsync();
        await Expect(first.GetByRole(AriaRole.Status).Filter(new() { HasText = "Signed out 1 other session." })).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });

        await second.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(second.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(second).ToHaveURLAsync(LoginUrl(), new() { Timeout = SecurityTab.ServerAnswerMs });
    }

    [E2EFact]
    public async Task The_list_follows_another_browser_signing_in_and_out_without_a_reload()
    {
        var first = await NewPageAsync();
        await first.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(first, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await SecurityTab.OpenAsync(first);
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(1, new() { Timeout = SecurityTab.ServerAnswerMs });
        await first.EvaluateAsync("() => { window.__weirNotReloaded = true; }");

        var second = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(second, BaseUrl);

        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first).Filter(new() { HasText = "This browser" })).ToHaveCountAsync(1);
        await second.GetByTestId("user-menu").ClickAsync();
        await second.GetByTestId("sign-out").ClickAsync();
        await Expect(second.GetByTestId("login-username")).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });

        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(1, new() { Timeout = SecurityTab.ServerAnswerMs });
        Assert.True(await first.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    [E2EFact]
    public async Task Signing_out_other_sessions_is_recorded_in_the_log()
    {
        var first = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(first, BaseUrl);
        var second = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(second, BaseUrl);
        var recordedBefore = await SecurityTab.RecordedEventCountAsync(first.Context, BaseUrl, SessionsRevoked);
        await SecurityTab.OpenAsync(first);
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });

        await SecurityTab.SignOutOthersButton(first).ClickAsync();
        await Expect(first.GetByRole(AriaRole.Status).Filter(new() { HasText = "Signed out 1 other session." })).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });

        Assert.Equal(recordedBefore + 1, await SecurityTab.RecordedEventCountAsync(first.Context, BaseUrl, SessionsRevoked));
        await Navigation.OpenLogsAsync(first, "Events");
        await Expect(first.GetByRole(AriaRole.Row).Filter(new() { HasText = "Sessions signed out" }).First).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
    }

    [E2EFact]
    public async Task Signing_out_one_session_ends_only_that_session()
    {
        var first = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(first, BaseUrl);
        var second = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(second, BaseUrl);
        var third = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(third, BaseUrl);
        await SecurityTab.OpenAsync(first);
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(3, new() { Timeout = SecurityTab.ServerAnswerMs });
        var signOutButtons = SecurityTab.SessionsTable(first).GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true });
        await Expect(signOutButtons).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first).Filter(new() { HasText = "This browser" }).GetByRole(AriaRole.Button)).ToHaveCountAsync(0);

        var revoke = await first.RunAndWaitForRequestAsync(
            async () => await signOutButtons.First.ClickAsync(),
            request => request.Method == "POST" && RevokeOneUrl().IsMatch(request.Url));
        Assert.Equal(200, (await revoke.ResponseAsync())!.Status);

        await Expect(first.GetByRole(AriaRole.Status).Filter(new() { HasText = "Session signed out." })).ToBeVisibleAsync(new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(SecurityTab.SessionRows(first)).ToHaveCountAsync(2, new() { Timeout = SecurityTab.ServerAnswerMs });
        await Expect(signOutButtons).ToHaveCountAsync(1, new() { Timeout = SecurityTab.ServerAnswerMs });
        var others = new[]
        {
            await SecurityTab.WhoAmIStatusAsync(second.Context, BaseUrl),
            await SecurityTab.WhoAmIStatusAsync(third.Context, BaseUrl),
        };
        Assert.Equal([200, 401], others.Order());
        Assert.Equal(200, await SecurityTab.WhoAmIStatusAsync(first.Context, BaseUrl));
    }

    [GeneratedRegex(@"/api/v1/auth/sessions/[0-9a-f-]+/revoke$")]
    private static partial Regex RevokeOneUrl();

    [GeneratedRegex(".*/login")]
    private static partial Regex LoginUrl();
}
