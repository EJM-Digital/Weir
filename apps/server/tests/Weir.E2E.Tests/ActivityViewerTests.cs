using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Activity for a viewer, who may look and may not change anything, in a real browser: the failed file is listed and its record opens, but
/// neither the button that retries every failed file nor any of the file's own buttons is offered, where the admin is offered them.
/// </summary>
public sealed partial class ActivityViewerTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private const float SignInMs = 60_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    private const string Film = "Nosferatu 1922.mkv";
    private const string Extra = "Gallery.mkv";

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task A_viewer_sees_a_failed_file_and_its_record_with_no_button_to_change_anything_and_the_admin_is_offered_them()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        await rig.AddViewerAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            await rig.HandOffReleaseAsync("h-failed", Film, Extra, failFirstPass: true);
            await Poll.UntilAsync(async () => (string?)(await rig.ListedFileAsync(Film))?["status"] == "processing_failed", "the film to fail", TimeSpan.FromMinutes(3));

            // The admin signs in first and sets the first-run wizard aside, as on any real install.
            var admin = await NewPageAsync(Wide);
            await Navigation.EnsureSignedInAsync(admin, rig.BaseUrl);
            var viewer = await NewPageAsync(Wide);
            await SignInAsViewerAsync(viewer, rig.BaseUrl);
            await viewer.GotoAsync($"{rig.BaseUrl}/activity?show=failed");
            var row = Rows(viewer).Filter(new() { HasText = Film });
            await Expect(row).ToContainTextAsync("Failed", new() { Timeout = PushMs });
            await row.GetByRole(AriaRole.Button).ClickAsync();

            await Expect(viewer.GetByTestId("activity-detail")).ToContainTextAsync(Film);
            await Expect(viewer.GetByTestId("activity-file-actions")).ToHaveCountAsync(0);
            await Expect(viewer.GetByRole(AriaRole.Button, new() { NameRegex = new("^(Try|Process|Pass through|Remove|Choose|Check again|Move to top)") })).ToHaveCountAsync(0);

            await admin.GotoAsync($"{rig.BaseUrl}/activity?show=failed");
            await Rows(admin).Filter(new() { HasText = Film }).GetByRole(AriaRole.Button).ClickAsync();

            await Expect(admin.GetByRole(AriaRole.Button, new() { Name = "Try the 1 failed file again", Exact = true })).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(admin.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true })).ToBeVisibleAsync();
            await Expect(viewer.GetByRole(AriaRole.Button, new() { Name = "Try the 1 failed file again", Exact = true })).ToHaveCountAsync(0);
        });
    }

    private static async Task SignInAsViewerAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("login-username").FillAsync(SeededAccounts.ViewerUsername);
        await page.GetByTestId("login-password").FillAsync(SeededAccounts.ViewerPassword);
        await page.GetByTestId("login-submit").ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = SignInMs });
    }

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });
}
