using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>Getting around Weir's shell the way a person does: sign in, the side menu, the tabs of an area.</summary>
public static partial class Navigation
{
    public const string BootstrapUser = "e2e-shell-admin";
    public const string BootstrapPassword = "e2e-shell-pass-min8";
    public const float UrlAssertMs = 20_000;

    // Creating the admin and signing in both hash a password (Argon2id, in the debug build the tests run, on a
    // machine that other work shares), and skipping the setup wizard saves several settings. Each submit waits for
    // its own form to go away, so a pending request is never clicked again and a failed one fails here instead of in
    // the next step. This is a ceiling, not a delay: a submit that settles sooner returns sooner.
    private const float SubmitSettleMs = 60_000;

    // First-run setup, sign-in, the setup wizard, then the shell: each screen at most once, plus spare turns
    // for a screen that was replaced between being seen and being checked.
    private const int MaxScreens = 6;

    /// <summary>Opens Weir and gets to the signed-in shell, creating the admin and skipping the setup wizard if asked.</summary>
    public static async Task EnsureSignedInAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl.TrimEnd('/')}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var setup = page.GetByTestId("setup-username");
        var login = page.GetByTestId("login-username");
        var wizardSkip = page.GetByTestId("setup-wizard-skip");
        var shell = page.GetByTestId("shell-ready");
        for (var screen = 0; screen < MaxScreens; screen++)
        {
            await Expect(setup.Or(login).Or(wizardSkip).Or(shell).First).ToBeVisibleAsync(new() { Timeout = UrlAssertMs });
            if (await shell.IsVisibleAsync())
            {
                return;
            }

            if (await setup.IsVisibleAsync())
            {
                await setup.FillAsync(BootstrapUser);
                await page.GetByTestId("setup-password").FillAsync(BootstrapPassword);
                await page.GetByTestId("setup-confirm-password").FillAsync(BootstrapPassword);
                await page.GetByTestId("setup-submit").ClickAsync();
                await Expect(setup).ToBeHiddenAsync(new() { Timeout = SubmitSettleMs });
            }
            else if (await login.IsVisibleAsync())
            {
                await login.FillAsync(BootstrapUser);
                await page.GetByTestId("login-password").FillAsync(BootstrapPassword);
                await page.GetByTestId("login-submit").ClickAsync();
                await Expect(login).ToBeHiddenAsync(new() { Timeout = SubmitSettleMs });
            }
            else if (await wizardSkip.IsVisibleAsync())
            {
                await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Set up Weir" })).ToBeVisibleAsync();
                await wizardSkip.ClickAsync();
                await Expect(wizardSkip).ToBeHiddenAsync(new() { Timeout = SubmitSettleMs });
            }
        }

        await Expect(shell).ToBeVisibleAsync(new() { Timeout = UrlAssertMs });
    }

    public static async Task OpenSidebarAsync(IPage page, string label) =>
        await SidebarLink(page, label).ClickAsync();

    /// <summary>
    /// A tab of a setup area (Workflows, Connections, Rules, Performance) or of System. Each area is an entry of its
    /// own in the side menu, which marks the one showing, and has its tabs across the top of the page.
    /// </summary>
    public static async Task OpenTabAsync(IPage page, string sidebar, string tab)
    {
        await OpenSidebarAsync(page, sidebar);
        await Expect(SidebarLink(page, sidebar)).ToHaveAttributeAsync("aria-current", "page");
        var selected = page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true });
        await selected.ClickAsync();
        await Expect(selected).ToHaveAttributeAsync("aria-selected", "true");
    }

    /// <summary>
    /// System › Logs: one log of Weir's events, its jobs and the server log. Each file's story is in Activity.
    /// <paramref name="source"/> is the name of one Source chip to press: "Events", "Jobs" or "Server".
    /// </summary>
    public static async Task OpenLogsAsync(IPage page, string? source = null)
    {
        await OpenTabAsync(page, "System", "Logs");
        await Expect(page.GetByTestId("log-summary")).ToBeVisibleAsync();
        if (!string.IsNullOrEmpty(source))
        {
            var chip = page.GetByRole(AriaRole.Group, new() { Name = "Source" })
                .GetByRole(AriaRole.Button, new() { NameRegex = StartsWith(source) });
            await chip.ClickAsync();
            await Expect(chip).ToHaveAttributeAsync("aria-pressed", "true");
        }
    }

    /// <summary>
    /// Every kind-of-file chip on Activity, in order, without their counts. Short of room the last chips fold into a
    /// "More" menu (at Playwright's 1280x720 the title line is shared with the pickers), so this reads the chips
    /// still on the title line, then opens More, reads what is folded into it, and closes it again. The chosen chip
    /// stays on the line wherever it sits, so the order is the page's own only while the first chip, All, is the
    /// chosen one.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ActivityChipLabelsAsync(IPage page)
    {
        var group = page.GetByRole(AriaRole.Group, new() { Name = "Show" });
        var onLine = await group.Locator("button.mm-segmented__option:not(.mm-more__button)").AllInnerTextsAsync();
        var more = group.GetByRole(AriaRole.Button, new() { Name = "More" });
        IReadOnlyList<string> folded = [];
        if (await more.CountAsync() > 0)
        {
            await more.ClickAsync();
            var menu = page.GetByRole(AriaRole.Menu, new() { Name = "More kinds of file" });
            await Expect(menu).ToBeVisibleAsync();
            folded = await menu.GetByRole(AriaRole.Menuitem).AllInnerTextsAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToBeHiddenAsync();
        }

        return [.. onLine.Concat(folded).Select(text => TrailingCount().Replace(text.Trim(), string.Empty))];
    }

    /// <summary>A link that carries a count is named for it ("Processing, 2 working", "Activity, 1 need you").</summary>
    private static ILocator SidebarLink(IPage page, string label) =>
        page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex($"^{Regex.Escape(label)}(?:,|$)") });

    private static Regex StartsWith(string text) => new($"^{Regex.Escape(text)}");

    [GeneratedRegex(@"\s+[\d,]+$")]
    private static partial Regex TrailingCount();
}
