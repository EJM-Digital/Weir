using System.Text.Json;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Creating the first account from another device asks for the setup code Weir wrote to its log and its data folder. Each test starts a
/// Weir of its own with no account, behind a reverse proxy on the same machine (WEIR_TRUSTED_PROXY_IPS) that tells Weir the browser's
/// address, which is how Weir comes to see a device that is not the one it runs on.
/// </summary>
public sealed class SetupCodeTests(E2EServer server) : E2ETestBase(server)
{
    private const string BootstrapPath = "/api/v1/auth/bootstrap";
    private const string AnotherDevice = "203.0.113.7";
    private const string WrongCodeReason =
        "This isn't the device Weir is running on. Enter the setup code from Weir's log or the setup-code file in its data folder.";

    private static readonly Dictionary<string, string> FromAnotherDevice = new() { ["X-Forwarded-For"] = AnotherDevice };

    private static Task<WeirServer> StartBehindProxyAsync() => E2EServer.StartWithSettingsAsync(("WEIR_TRUSTED_PROXY_IPS", "127.0.0.1"));

    private static string CodeFile(WeirServer weir) => Path.Combine(weir.Home, "setup-code");

    private static async Task<string> WrittenCodeAsync(WeirServer weir) => (await File.ReadAllTextAsync(CodeFile(weir))).Trim();

    private static async Task FillAsync(IPage page, string code)
    {
        await page.GetByTestId("setup-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("setup-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-code").FillAsync(code);
    }

    private static async Task<bool> AccountCreationOpenAsync(IBrowserContext context, WeirServer weir)
    {
        await using var response = await context.APIRequest.GetAsync($"{weir.BaseUrl.GetLeftPart(UriPartial.Authority)}/api/v1/auth/bootstrap/status");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("bootstrap_allowed").GetBoolean();
    }

    [E2EFact]
    public async Task From_another_device_the_page_asks_for_the_code_refuses_a_wrong_one_and_accepts_the_one_Weir_wrote()
    {
        await using var weir = await StartBehindProxyAsync();
        var baseUrl = weir.BaseUrl.GetLeftPart(UriPartial.Authority);
        var code = await WrittenCodeAsync(weir);
        Assert.Contains(code, weir.LogText(), StringComparison.Ordinal);
        var page = await NewPageAsync(headers: FromAnotherDevice);
        await page.GotoAsync($"{baseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var field = page.GetByTestId("setup-code");
        await Expect(field).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(field).ToHaveAttributeAsync("required", string.Empty);
        await Expect(page.GetByText("Shown in Weir's log (docker logs) and saved in the setup-code file in Weir's data folder.", new() { Exact = true })).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "The first-run page asking for the setup code");

        await FillAsync(page, "WRNG-CODE");
        var refused = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByTestId("setup-submit").ClickAsync(),
            response => response.Url.EndsWith(BootstrapPath, StringComparison.Ordinal),
            new() { Timeout = Navigation.SubmitSettleMs });

        Assert.Equal(403, refused.Status);
        await Expect(SetupWizard.Alert(page.GetByTestId("setup-form"))).ToHaveTextAsync(WrongCodeReason);
        await Expect(field).ToHaveValueAsync("WRNG-CODE");
        Assert.True(await AccountCreationOpenAsync(page.Context, weir));
        Assert.Equal(code, await WrittenCodeAsync(weir));

        await field.FillAsync($"  {code.ToLowerInvariant()}  ");
        var created = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByTestId("setup-submit").ClickAsync(),
            request => request.Method == "POST" && request.Url.EndsWith(BootstrapPath, StringComparison.Ordinal));

        Assert.Equal(code.ToLowerInvariant(), JsonDocument.Parse(created.PostData!).RootElement.GetProperty("setup_code").GetString());
        await SetupWizard.SaveRecoveryCodesAsync(page);
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.False(await AccountCreationOpenAsync(page.Context, weir));
        Assert.False(File.Exists(CodeFile(weir)), "The setup code file was left behind after the account was made.");
    }

    [E2EFact]
    public async Task From_the_device_Weir_runs_on_there_is_no_code_to_enter_even_while_Weir_holds_one()
    {
        await using var weir = await StartBehindProxyAsync();
        var baseUrl = weir.BaseUrl.GetLeftPart(UriPartial.Authority);
        Assert.True(File.Exists(CodeFile(weir)));
        var page = await NewPageAsync();
        await page.GotoAsync($"{baseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("setup-username")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page.GetByTestId("setup-code")).ToHaveCountAsync(0);

        await page.GetByTestId("setup-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("setup-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-submit").ClickAsync();

        await SetupWizard.SaveRecoveryCodesAsync(page);
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.False(File.Exists(CodeFile(weir)));
    }
}
