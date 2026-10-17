using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// A viewer who signs in before an administrator has finished the first-run wizard is told so, and is never sent to a wizard whose
/// every button the server would refuse.
/// </summary>
public sealed class ViewerBeforeSetupTests(E2EServer server) : E2ETestBase(server)
{
    private const string NoticeHeading = "Weir isn't set up yet";

    private async Task<IPage> SignedInViewerAsync()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        E2EDatabase.InsertViewer(Server.DatabasePath);
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("login-username").FillAsync(SeededAccounts.ViewerUsername);
        await page.GetByTestId("login-password").FillAsync(SeededAccounts.ViewerPassword);
        await page.GetByTestId("login-submit").ClickAsync();
        return page;
    }

    [E2EFact]
    public async Task A_viewer_signing_in_before_setup_is_finished_is_told_an_administrator_must_finish_it()
    {
        var page = await SignedInViewerAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = NoticeHeading })).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByText("An administrator needs to finish setting Weir up first.")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("setup-wizard-skip")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        Assert.DoesNotContain("/setup-wizard", page.Url, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task A_viewer_who_opens_the_wizard_address_is_told_the_same_and_is_not_refused()
    {
        var page = await SignedInViewerAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = NoticeHeading })).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        await page.GotoAsync($"{BaseUrl}/setup-wizard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = NoticeHeading })).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByTestId("setup-wizard-skip")).ToHaveCountAsync(0);
        await Expect(page.GetByText("Forbidden")).ToHaveCountAsync(0);
    }
}
