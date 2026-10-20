using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The first-run wizard against the real server, with folders typed by hand: what Finish saves and shows, the backup boxes, the button's
/// two names, what keeps Finish from going through, Skip, and the warning before leaving. Connecting a download client or media manager
/// in the wizard is covered by the wizard's own component tests in apps/web/src/pages/setup.
/// </summary>
public sealed class SetupWizardTests(E2EServer server) : E2ETestBase(server)
{
    private const string LibrariesPath = "/api/v1/processing/libraries";
    private const string SettingsPath = "/api/v1/suite/settings";

    private async Task<IPage> OpenWizardAsync(ViewportSize? viewport = null)
    {
        var page = await NewPageAsync(viewport);
        await SetupWizard.CreateAccountAsync(page, BaseUrl);
        return page;
    }

    private static async Task TypeFoldersAsync(IPage page, string movieWatched, string movieOutput, string tvWatched, string tvOutput)
    {
        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(movieWatched);
        await SetupWizard.Folder(page, "Movies output folder").FillAsync(movieOutput);
        await SetupWizard.Folder(page, "TV watched folder").FillAsync(tvWatched);
        await SetupWizard.Folder(page, "TV output folder").FillAsync(tvOutput);
    }

    private static async Task ChooseTimeZoneAsync(IPage page, string city)
    {
        await SetupWizard.TimeZone(page).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { NameRegex = new Regex(city) }).ClickAsync();
    }

    private static JsonElement BodyOf(SentRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    private static string[] Lines(IEnumerable<SentRequest> sent) => [.. sent.Select(request => $"{request.Method} {request.PathAndQuery}")];

    /// <summary>Finishes the wizard with nothing typed (the "Neither" answer), which only marks setup done.</summary>
    private static async Task FinishWithNothingTypedAsync(IPage page)
    {
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
    }

    private static ILocator CheckForUpdates(IPage page) => page.GetByRole(AriaRole.Checkbox, new() { Name = "Check for updates", Exact = true });

    [E2EFact]
    public async Task The_Windows_app_asks_whether_to_check_for_updates_starts_with_no_and_saves_a_yes_before_it_marks_setup_done()
    {
        await using var weir = await E2EServer.StartWindowsInstallAsync(checksForUpdates: false);
        var page = await NewPageAsync();
        var requests = RequestLog.Watch(page);
        await SetupWizard.CreateAccountAsync(page, weir.BaseUrl.GetLeftPart(UriPartial.Authority));
        await Expect(CheckForUpdates(page)).Not.ToBeCheckedAsync();
        await Expect(page.GetByText("It checks nothing unless you say so.", new() { Exact = false })).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "The wizard asking whether to check for updates");
        await CheckForUpdates(page).CheckAsync();
        var mark = requests.Mark();

        await FinishWithNothingTypedAsync(page);

        var sent = requests.Since(mark);
        Assert.Equal([$"PUT /api/v1/suite/update-settings", $"PUT {SettingsPath}"], Lines(sent));
        Assert.True(BodyOf(sent[0]).GetProperty("check_for_updates").GetBoolean());
        Assert.Contains("\"checkForUpdates\": true", File.ReadAllText(Path.Join(weir.Home, "update-settings.json")), StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task A_Docker_install_is_not_asked_whether_to_check_for_updates_and_saves_nothing_about_it()
    {
        await using var weir = await E2EServer.StartOwnAsync(("WEIR_RUNTIME", "docker"));
        var page = await NewPageAsync();
        var requests = RequestLog.Watch(page);
        await SetupWizard.CreateAccountAsync(page, weir.BaseUrl.GetLeftPart(UriPartial.Authority));
        await Expect(page.GetByRole(AriaRole.Radio)).ToHaveCountAsync(4);
        await Expect(CheckForUpdates(page)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Updates", Exact = true })).ToHaveCountAsync(0);
        var mark = requests.Mark();

        await FinishWithNothingTypedAsync(page);

        Assert.Equal([$"PUT {SettingsPath}"], Lines(requests.Since(mark)));
        Assert.False(File.Exists(Path.Join(weir.Home, "update-settings.json")));
    }

    [E2EFact]
    public async Task Finishing_with_typed_folders_fills_in_the_first_workflow_of_each_type_saves_the_settings_and_says_whats_next()
    {
        using var media = new TemporaryFolder();
        var folders = new[] { "movies-in", "movies-out", "tv-in", "tv-out" }.Select(name => Directory.CreateDirectory(Path.Join(media.Path, name)).FullName).ToArray();
        var page = await OpenWizardAsync();
        var requests = RequestLog.Watch(page);
        await Expect(SetupWizard.FinishButton(page)).ToHaveTextAsync(SetupWizard.FinishLabel);
        await Expect(page.GetByRole(AriaRole.Radio)).ToHaveCountAsync(4);
        await Expect(SetupWizard.Folder(page, "Movies watched folder")).ToHaveCountAsync(0);
        await Axe.AssertNoViolationsAsync(page, "The wizard before an answer");

        await SetupWizard.Source(page, "Neither").CheckAsync();
        await TypeFoldersAsync(page, folders[0], folders[1], folders[2], folders[3]);
        await ChooseTimeZoneAsync(page, "New York");
        await SetupWizard.BackupToggle(page).CheckAsync();
        await SetupWizard.BackupInterval(page).SelectOptionAsync("168");
        await SetupWizard.BackupTime(page).FillAsync("03:30");
        await Axe.AssertNoViolationsAsync(page, "The wizard with folders, a time zone and backups filled in");
        var movie = await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie");
        var tv = await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "tv");
        var mark = requests.Mark();

        await SetupWizard.FinishButton(page).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        var sent = requests.Since(mark);
        Assert.Equal(
            [$"PUT {LibrariesPath}/{(long)movie["id"]!}", $"PUT {LibrariesPath}/{(long)tv["id"]!}", $"PUT {SettingsPath}"],
            Lines(sent));
        Assert.Equal(folders[0], BodyOf(sent[0]).GetProperty("watched_folder").GetString());
        Assert.Equal(folders[1], BodyOf(sent[0]).GetProperty("output_folder").GetString());
        Assert.Equal(folders[2], BodyOf(sent[1]).GetProperty("watched_folder").GetString());
        Assert.Equal(folders[3], BodyOf(sent[1]).GetProperty("output_folder").GetString());
        var settingsSent = BodyOf(sent[2]);
        Assert.Equal("completed", settingsSent.GetProperty("setup_wizard_state").GetString());
        Assert.Equal("America/New_York", settingsSent.GetProperty("app_timezone").GetString());
        Assert.True(settingsSent.GetProperty("configuration_backup_enabled").GetBoolean());
        Assert.Equal(168, settingsSent.GetProperty("configuration_backup_interval_hours").GetInt32());
        Assert.Equal("03:30", settingsSent.GetProperty("configuration_backup_preferred_time").GetString());
        Assert.All(sent, request => Assert.True(request.CsrfHeader is not null || BodyOf(request).TryGetProperty("csrf_token", out _), $"{request} carried no CSRF token."));

        var savedMovie = await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie");
        var savedTv = await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "tv");
        Assert.Equal([folders[0], folders[1], folders[2], folders[3]], [(string)savedMovie["watched_folder"]!, (string)savedMovie["output_folder"]!, (string)savedTv["watched_folder"]!, (string)savedTv["output_folder"]!]);
        Assert.Equal((long)movie["id"]!, (long)savedMovie["id"]!);
        Assert.Equal(2, (await SetupState.WorkflowsAsync(page.Context, BaseUrl)).Count(workflow => (string)workflow["media_type"]! is "movie" or "tv"));
        var settings = await SetupState.SettingsAsync(page.Context, BaseUrl);
        Assert.Equal("completed", (string)settings["setup_wizard_state"]!);
        Assert.Equal("America/New_York", (string)settings["app_timezone"]!);
        Assert.True((bool)settings["configuration_backup_enabled"]!);
        Assert.Equal(168, (int)settings["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string)settings["configuration_backup_preferred_time"]!);

        await Axe.AssertNoViolationsAsync(page, "What's next");
        await page.GetByRole(AriaRole.Link, new() { Name = "Continue to Weir", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/");
    }

    [E2EFact]
    public async Task The_backup_boxes_are_off_until_backups_are_switched_on_keep_what_was_chosen_and_are_saved_as_chosen()
    {
        var page = await OpenWizardAsync();
        var toggle = SetupWizard.BackupToggle(page);
        var interval = SetupWizard.BackupInterval(page);
        var time = SetupWizard.BackupTime(page);
        await Expect(toggle).Not.ToBeCheckedAsync();
        await Expect(interval).ToBeDisabledAsync();
        await Expect(time).ToBeDisabledAsync();
        await Expect(interval).ToHaveValueAsync("24");
        await Expect(time).ToHaveValueAsync("02:00");
        Assert.Equal(["Every day", "Every 2 days", "Every week"], await interval.Locator("option").AllInnerTextsAsync());

        await toggle.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Expect(toggle).ToBeCheckedAsync();
        await Expect(interval).ToBeEnabledAsync();
        await Expect(time).ToBeEnabledAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(interval).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(interval).ToHaveValueAsync("48");
        await time.FillAsync("04:15");
        await time.FillAsync(string.Empty);
        await Expect(time).ToHaveValueAsync("02:00");
        await time.FillAsync("04:15");
        await Axe.AssertNoViolationsAsync(page, "The wizard with backups on");

        await toggle.UncheckAsync();
        await Expect(interval).ToBeDisabledAsync();
        await Expect(time).ToBeDisabledAsync();
        await Expect(interval).ToHaveValueAsync("48");
        await Expect(time).ToHaveValueAsync("04:15");
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });

        var off = await SetupState.SettingsAsync(page.Context, BaseUrl);
        Assert.False((bool)off["configuration_backup_enabled"]!);
        Assert.Equal(48, (int)off["configuration_backup_interval_hours"]!);
        Assert.Equal("04:15", (string)off["configuration_backup_preferred_time"]!);

        await page.GotoAsync($"{BaseUrl}/setup-wizard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(toggle).Not.ToBeCheckedAsync();
        await Expect(interval).ToHaveValueAsync("48");
        await Expect(time).ToHaveValueAsync("04:15");
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await toggle.CheckAsync();
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.True((bool)(await SetupState.SettingsAsync(page.Context, BaseUrl))["configuration_backup_enabled"]!);
    }

    [E2EFact]
    public async Task The_button_says_Finish_setup_until_setup_is_done_and_the_reopened_wizard_says_Save_changes_and_saves_only_the_settings()
    {
        var page = await OpenWizardAsync();
        await Expect(SetupWizard.FinishButton(page)).ToHaveTextAsync(SetupWizard.FinishLabel);
        await FinishWithNothingTypedAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "Continue to Weir", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        var requests = RequestLog.Watch(page);

        await Navigation.OpenSidebarAsync(page, "System");
        await page.GetByTestId("suite-settings-open-setup-wizard").ClickAsync();

        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync();
        await Expect(SetupWizard.FinishButton(page)).ToHaveTextAsync(SetupWizard.SaveLabel);
        await Expect(SetupWizard.Source(page, "Neither")).Not.ToBeCheckedAsync();
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.BackupToggle(page).CheckAsync();
        var mark = requests.Mark();
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.Equal([$"PUT {SettingsPath}"], Lines(requests.Since(mark)));
        var settings = await SetupState.SettingsAsync(page.Context, BaseUrl);
        Assert.Equal("completed", (string)settings["setup_wizard_state"]!);
        Assert.True((bool)settings["configuration_backup_enabled"]!);
    }

    [E2EFact]
    public async Task Skipping_the_reopened_wizard_changes_nothing_and_does_not_turn_finished_setup_into_skipped()
    {
        var page = await OpenWizardAsync();
        await FinishWithNothingTypedAsync(page);
        await page.GotoAsync($"{BaseUrl}/setup-wizard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(SetupWizard.FinishButton(page)).ToHaveTextAsync(SetupWizard.SaveLabel, new() { Timeout = Navigation.UrlAssertMs });

        await SetupWizard.Skip(page).ClickAsync();

        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.Equal("completed", (string)(await SetupState.SettingsAsync(page.Context, BaseUrl))["setup_wizard_state"]!);
    }

    [E2EFact]
    public async Task Whats_next_offers_the_next_steps_and_each_one_goes_where_it_says()
    {
        var page = await OpenWizardAsync();
        await FinishWithNothingTypedAsync(page);
        var steps = new (string Link, Func<Task> Lands)[]
        {
            ("Choose which tracks to keep", async () => await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Profiles", Exact = true })).ToHaveAttributeAsync("aria-selected", "true")),
            ("Connect Sonarr, Radarr or Deluno", async () => await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Media managers", Exact = true })).ToHaveAttributeAsync("aria-selected", "true")),
            ("Clean files you already have", async () => await Expect(page).ToHaveURLAsync($"{BaseUrl}/library")),
        };

        foreach (var (link, lands) in steps)
        {
            await Expect(page.GetByRole(AriaRole.List).GetByRole(AriaRole.Link)).ToHaveCountAsync(3);
            await page.GetByRole(AriaRole.Link, new() { Name = link, Exact = true }).ClickAsync();
            await lands();
            await page.GotoAsync($"{BaseUrl}/setup-wizard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(SetupWizard.FinishButton(page)).ToHaveTextAsync(SetupWizard.SaveLabel, new() { Timeout = Navigation.UrlAssertMs });
            await SetupWizard.Source(page, "Neither").CheckAsync();
            await SetupWizard.FinishButton(page).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        }
    }

    [E2EFact]
    public async Task Finish_is_held_back_in_words_until_the_answer_and_the_output_folders_are_given_and_nothing_is_sent_meanwhile()
    {
        using var media = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(media.Path, "in")).FullName;
        var page = await OpenWizardAsync();
        var requests = RequestLog.Watch(page);
        var mark = requests.Mark();

        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Choose how your downloads reach Weir first. “Neither” lets you pick the folders yourself.");

        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(watched);
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Add a Movies output folder: it is needed when a Movies watched folder is set.");

        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(string.Empty);
        await SetupWizard.Folder(page, "TV watched folder").FillAsync(watched);
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Add a TV output folder: it is needed when a TV watched folder is set.");
        await Axe.AssertNoViolationsAsync(page, "The wizard with a reason shown");

        requests.AssertNothingSentSince(mark);
        Assert.Equal("pending", (string)(await SetupState.SettingsAsync(page.Context, BaseUrl))["setup_wizard_state"]!);
    }

    [E2EFact]
    public async Task A_folder_the_server_refuses_is_shown_in_its_words_keeps_setup_unfinished_and_can_then_be_corrected()
    {
        using var media = new TemporaryFolder();
        var outputFolder = Directory.CreateDirectory(Path.Join(media.Path, "out")).FullName;
        var watchedFolder = Directory.CreateDirectory(Path.Join(media.Path, "in")).FullName;
        var page = await OpenWizardAsync();
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(Server.Home);
        await SetupWizard.Folder(page, "Movies output folder").FillAsync(outputFolder);

        await SetupWizard.FinishButton(page).ClickAsync();

        await Expect(SetupWizard.Alert(page)).ToHaveTextAsync("Weir can't use its own data folder as a watched folder. Choose a different folder.", new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(SetupWizard.FinishButton(page)).ToBeEnabledAsync();
        await Expect(SetupWizard.Folder(page, "Movies watched folder")).ToHaveValueAsync(Server.Home);
        Assert.Equal("pending", (string)(await SetupState.SettingsAsync(page.Context, BaseUrl))["setup_wizard_state"]!);
        Assert.NotEqual(Server.Home, (string)(await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie"))["watched_folder"]!);

        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(watchedFolder);
        await SetupWizard.FinishButton(page).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.Equal("completed", (string)(await SetupState.SettingsAsync(page.Context, BaseUrl))["setup_wizard_state"]!);
    }

    [E2EFact]
    public async Task Skipping_marks_setup_skipped_changes_no_workflow_and_goes_to_the_Dashboard_for_good()
    {
        using var media = new TemporaryFolder();
        var typed = Directory.CreateDirectory(Path.Join(media.Path, "in")).FullName;
        var page = await OpenWizardAsync();
        var requests = RequestLog.Watch(page);
        await SetupWizard.Source(page, "Neither").CheckAsync();
        await SetupWizard.Folder(page, "Movies watched folder").FillAsync(typed);
        await SetupWizard.BackupToggle(page).CheckAsync();
        var before = await SetupState.WorkflowsAsync(page.Context, BaseUrl);
        var mark = requests.Mark();

        await SetupWizard.Skip(page).ClickAsync();

        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/");
        Assert.Equal([$"PUT {SettingsPath}"], Lines(requests.Since(mark)));
        Assert.Equal("skipped", BodyOf(requests.Since(mark)[0]).GetProperty("setup_wizard_state").GetString());
        var settings = await SetupState.SettingsAsync(page.Context, BaseUrl);
        Assert.Equal("skipped", (string)settings["setup_wizard_state"]!);
        Assert.False((bool)settings["configuration_backup_enabled"]!);
        Assert.Equal(before.Select(workflow => workflow.ToJsonString()), (await SetupState.WorkflowsAsync(page.Context, BaseUrl)).Select(workflow => workflow.ToJsonString()));

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });
        await Expect(page).ToHaveURLAsync($"{BaseUrl}/");
    }

    [E2EFact]
    public async Task Leaving_before_setup_is_finished_or_skipped_asks_the_browser_to_confirm_and_afterwards_does_not()
    {
        var page = await OpenWizardAsync();
        await SetupWizard.Source(page, "Neither").CheckAsync();
        Assert.True(await LeavingIsQueriedAsync(page), "Leaving the unfinished wizard was not held for the browser to confirm.");

        await SetupWizard.Skip(page).ClickAsync();
        await Expect(page.GetByTestId("shell-ready")).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        await page.GotoAsync($"{BaseUrl}/setup-wizard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(SetupWizard.Heading(page)).ToBeVisibleAsync(new() { Timeout = Navigation.UrlAssertMs });

        Assert.False(await LeavingIsQueriedAsync(page), "Leaving the wizard was held although setup is no longer pending.");
    }

    /// <summary>Whether a page that is about to be left asks the browser to confirm: the page cancels the event the browser sends it first.</summary>
    private static async Task<bool> LeavingIsQueriedAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => { const leaving = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(leaving); return leaving.defaultPrevented; }");

    [E2EFact]
    public async Task The_wizard_fits_a_phone_and_is_worked_by_keyboard()
    {
        var page = await OpenWizardAsync(new ViewportSize { Width = 375, Height = 812 });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The page scrolls sideways.");
        await Axe.AssertNoViolationsAsync(page, "The wizard at phone width");

        await SetupWizard.Source(page, "Neither").FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Expect(SetupWizard.Source(page, "Neither")).ToBeCheckedAsync();
        await SetupWizard.TimeZone(page).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByRole(AriaRole.Listbox)).ToHaveCountAsync(0);
        await SetupWizard.FinishButton(page).FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What's next", Exact = true })).ToBeVisibleAsync(new() { Timeout = Navigation.SubmitSettleMs });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "What's next scrolls sideways.");
        await Axe.AssertNoViolationsAsync(page, "What's next at phone width");
    }
}
