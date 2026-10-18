using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>The first-run screens: creating the account the way a person does, and finding the wizard's controls.</summary>
public static partial class SetupWizard
{
    public const string FinishLabel = "Finish setup";
    public const string SaveLabel = "Save changes";

    /// <summary>On a fresh install, creates the account on the first-run page and waits for the wizard that follows it.</summary>
    public static async Task CreateAccountAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("setup-username").FillAsync(Navigation.BootstrapUser);
        await page.GetByTestId("setup-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(Navigation.BootstrapPassword);
        await page.GetByTestId("setup-submit").ClickAsync();
        await Expect(Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
    }

    public static ILocator Heading(IPage page) => page.GetByRole(AriaRole.Heading, new() { Name = "Set up Weir", Exact = true });

    /// <summary>The button that saves the wizard: "Finish setup" the first time, "Save changes" once setup is done.</summary>
    public static ILocator FinishButton(IPage page) => page.GetByRole(AriaRole.Button, new() { NameRegex = FinishOrSave() });

    public static ILocator Skip(IPage page) => page.GetByTestId("setup-wizard-skip");

    /// <summary>The "How do your downloads reach Weir?" answer by its words ("Neither", ...).</summary>
    public static ILocator Source(IPage page, string answer) => page.GetByRole(AriaRole.Radio, new() { NameRegex = new Regex(answer) });

    /// <summary>A folder box by its full name, such as "Movies watched folder".</summary>
    public static ILocator Folder(IPage page, string name) => page.GetByRole(AriaRole.Textbox, new() { Name = name, Exact = true });

    public static ILocator Alert(IPage page) => page.GetByRole(AriaRole.Alert);

    public static ILocator Alert(ILocator scope) => scope.GetByRole(AriaRole.Alert);

    public static ILocator BackupToggle(IPage page) => page.GetByRole(AriaRole.Checkbox, new() { Name = "Back up the configuration automatically", Exact = true });

    public static ILocator BackupInterval(IPage page) => page.GetByRole(AriaRole.Combobox, new() { Name = "Minimum time between backups", Exact = true });

    public static ILocator BackupTime(IPage page) => page.GetByLabel("Preferred time", new() { Exact = true });

    public static ILocator TimeZone(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Time zone", Exact = true });

    [GeneratedRegex("^(?:Finish setup|Save changes)$")]
    private static partial Regex FinishOrSave();
}
