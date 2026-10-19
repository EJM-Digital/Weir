using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Performance › Cleanup against the real server: each switch, list, field and button sends the request it is for, the server holds the
/// result, and the table follows the server without a reload. Nothing is intercepted.
/// </summary>
public sealed class CleanupTabTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = $"{WeirClient.Api}/processing/operator-settings";
    private const string Run = $"{WeirClient.Api}/processing/maintenance/run";

    private static readonly string[] EveryLabels = ["15 minutes", "30 minutes", "1 hour", "6 hours", "12 hours", "1 day", "7 days"];

    private static ILocator Dialog(IPage page, string family, string action) => page.GetByTestId($"processing-maintenance-confirm-{family}-{action}");

    private static ILocator RadioIn(ILocator group, string side) => group.GetByRole(AriaRole.Radio, new() { Name = side, Exact = true });

    private static async Task<string> NextRunWordsAsync(IPage page, IBrowserContext context, string baseUrl, string family)
    {
        var state = await CleanupTab.MaintenanceAsync(context, baseUrl, family);
        if (!state.GetProperty("enabled").GetBoolean())
        {
            return "Off";
        }

        return state.GetProperty("next_run_at").ValueKind == System.Text.Json.JsonValueKind.Null
            ? "Within half a minute"
            : await AppDates.ShownAsync(page, state.GetProperty("next_run_at").GetString()!, "UTC");
    }

    private static async Task<(string? Completed, bool Idle)> RunStateAsync(IBrowserContext context, string baseUrl, string family)
    {
        var state = await CleanupTab.MaintenanceAsync(context, baseUrl, family);
        var completed = state.GetProperty("last_completed_at").ValueKind == System.Text.Json.JsonValueKind.Null ? null : state.GetProperty("last_completed_at").GetString();
        return (completed, state.GetProperty("pending").GetInt32() == 0 && state.GetProperty("running").GetInt32() == 0);
    }

    [E2EFact]
    public async Task A_job_that_deletes_nothing_is_switched_off_and_on_at_once_with_no_question()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);
        var group = CleanupTab.Switch(page, CleanupTab.LeftoverName);
        await Expect(RadioIn(group, "On")).ToHaveAttributeAsync("aria-checked", "true");

        await RadioIn(group, "Off").ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Leftover work files is off.");
        await Expect(RadioIn(group, "Off")).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='nextRun']")).ToHaveTextAsync("Off");
        Assert.Equal([CleanupTab.SettingsRequest], await sent.SoFarAsync());
        Assert.Equal(["""{"work_temp_stale_sweep_enabled":false}"""], (await sent.BodiesAsync(CleanupTab.SettingsRequest)).Select(body => body.ToJsonString()));
        Assert.False((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("work_temp_stale_sweep_enabled").GetBoolean());

        await RadioIn(group, "On").ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Leftover work files is on. It first runs within half a minute.");
        await Expect(RadioIn(group, "On")).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        Assert.Equal(
            ["""{"work_temp_stale_sweep_enabled":false}""", """{"work_temp_stale_sweep_enabled":true}"""],
            (await sent.BodiesAsync(CleanupTab.SettingsRequest)).Select(body => body.ToJsonString()));
        Assert.True((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("work_temp_stale_sweep_enabled").GetBoolean());
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='nextRun']")).Not.ToHaveTextAsync("Off");
    }

    [E2EFact]
    public async Task Switching_on_a_job_that_deletes_files_asks_first_and_Escape_or_Keep_it_leaves_it_off_and_sends_nothing()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);
        var group = CleanupTab.Switch(page, CleanupTab.UnclaimedName);
        var dialog = Dialog(page, CleanupTab.Unclaimed, "enable");
        await Expect(RadioIn(group, "Off")).ToHaveAttributeAsync("aria-checked", "true");

        await RadioIn(group, "On").ClickAsync();

        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync("Switch on \"Cleaned copies nobody picked up\"?");
        await Expect(dialog).ToContainTextAsync("Files it deletes cannot be recovered.");
        await Expect(page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-enable-cancel")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);

        await RadioIn(group, "On").ClickAsync();
        await page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-enable-cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        Assert.Empty(await sent.SoFarAsync());
        await Expect(RadioIn(group, "Off")).ToHaveAttributeAsync("aria-checked", "true");
        Assert.False((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("unclaimed_handback_cleanup_enabled").GetBoolean());

        await RadioIn(group, "On").ClickAsync();
        await page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-enable-confirm").ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Cleaned copies nobody picked up is on. It first runs within half a minute.");
        await Expect(RadioIn(group, "On")).ToHaveAttributeAsync("aria-checked", "true");
        Assert.Equal([CleanupTab.SettingsRequest], await sent.SoFarAsync());
        Assert.Equal(["""{"unclaimed_handback_cleanup_enabled":true}"""], (await sent.BodiesAsync(CleanupTab.SettingsRequest)).Select(body => body.ToJsonString()));
        Assert.True((await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("unclaimed_handback_cleanup_enabled").GetBoolean());
    }

    [E2EFact]
    public async Task Running_a_job_that_deletes_files_asks_first_and_runs_it_for_movies_and_tv_only_when_confirmed()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);
        var dialog = Dialog(page, CleanupTab.Unclaimed, "run");
        var cancel = page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-run-cancel");

        await CleanupTab.RunNow(page, CleanupTab.Unclaimed).ClickAsync();
        await Expect(dialog).ToContainTextAsync("Run \"Cleaned copies nobody picked up\" now?");
        await Expect(cancel).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);

        await CleanupTab.RunNow(page, CleanupTab.Unclaimed).ClickAsync();
        await cancel.ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        Assert.Empty(await sent.SoFarAsync());

        await CleanupTab.RunNow(page, CleanupTab.Unclaimed).ClickAsync();
        await page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-run-confirm").ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Cleaned copies nobody picked up: started. Its result shows under Last run.");
        Assert.Equal([CleanupTab.RunRequest, CleanupTab.RunRequest], await sent.SoFarAsync());
        Assert.Equal(
            ["""{"family":"unclaimed_handbacks","media_scope":"movie"}""", """{"family":"unclaimed_handbacks","media_scope":"tv"}"""],
            (await sent.BodiesAsync(CleanupTab.RunRequest)).Select(body => body.ToJsonString()));
    }

    [E2EFact]
    public async Task Run_now_on_a_job_that_deletes_nothing_starts_it_at_once_and_its_result_shows_under_Last_run_without_a_reload()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var before = (await RunStateAsync(page.Context, BaseUrl, CleanupTab.Leftover)).Completed;
        var sent = ChangesSent.Watch(page);

        await CleanupTab.RunNow(page, CleanupTab.Leftover).ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Leftover work files: started. Its result shows under Last run.");
        await Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        Assert.Equal([CleanupTab.RunRequest, CleanupTab.RunRequest], await sent.SoFarAsync());
        Assert.Equal(
            ["""{"family":"work_temp_stale_sweep","media_scope":"movie"}""", """{"family":"work_temp_stale_sweep","media_scope":"tv"}"""],
            (await sent.BodiesAsync(CleanupTab.RunRequest)).Select(body => body.ToJsonString()));

        string? completed = null;
        await Poll.UntilAsync(
            async () =>
            {
                var (latest, idle) = await RunStateAsync(page.Context, BaseUrl, CleanupTab.Leftover);
                completed = latest;
                return idle && latest != before;
            },
            "the manual Leftover work files run to finish");
        var lastRun = CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='lastRun']");
        await Expect(lastRun).ToHaveTextAsync(await AppDates.ShownAsync(page, completed!, "UTC"));
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    [E2EFact]
    public async Task How_often_saves_the_interval_says_so_and_a_saved_interval_the_list_lacks_is_added_to_it()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);
        var every = CleanupTab.Every(page, CleanupTab.LeftoverName);
        await Expect(every.Locator("option")).ToHaveTextAsync(EveryLabels);
        await Expect(every).ToHaveValueAsync("3600");
        await Expect(CleanupTab.Every(page, CleanupTab.UnclaimedName)).ToHaveValueAsync("21600");

        await every.SelectOptionAsync("21600");

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Leftover work files now runs every 6 hours.");
        Assert.Equal([CleanupTab.SettingsRequest], await sent.SoFarAsync());
        Assert.Equal(["""{"work_temp_stale_sweep_interval_seconds":21600}"""], (await sent.BodiesAsync(CleanupTab.SettingsRequest)).Select(body => body.ToJsonString()));
        Assert.Equal(21600, (await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Leftover)).GetProperty("interval_seconds").GetInt32());

        var odd = await admin.PutWithCsrfAsync(Settings, new JsonObject { ["unclaimed_handback_cleanup_interval_seconds"] = 5400 });
        Assert.True(odd.Status == System.Net.HttpStatusCode.OK, odd.ToString());
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var unclaimed = CleanupTab.Every(page, CleanupTab.UnclaimedName);

        await Expect(unclaimed).ToHaveValueAsync("5400");
        await Expect(unclaimed.Locator("option")).ToHaveTextAsync([.. EveryLabels, "90 minutes"]);
        await Expect(unclaimed.Locator("option:checked")).ToHaveTextAsync("90 minutes");
    }

    [E2EFact]
    public async Task How_long_copies_wait_offers_Save_only_for_a_changed_number_from_1_to_365()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);
        var field = CleanupTab.DaysField(page);
        var save = page.GetByTestId("processing-maintenance-handback-window").GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await Expect(field).ToHaveValueAsync("14");
        await Expect(save).ToHaveCountAsync(0);

        foreach (var refused in new[] { "0", "366", string.Empty, "14" })
        {
            await field.FillAsync(refused);
            await Expect(save).ToHaveCountAsync(0);
        }

        await field.FillAsync("30");
        await Expect(save).ToBeVisibleAsync();
        await save.ClickAsync();

        await Expect(CleanupTab.Notice(page)).ToHaveTextAsync("Cleaned copies nobody picked up now wait 30 days.");
        await Expect(save).ToHaveCountAsync(0);
        await Expect(field).ToHaveValueAsync("30");
        Assert.Equal([CleanupTab.SettingsRequest], await sent.SoFarAsync());
        Assert.Equal(["""{"unclaimed_handback_window_days":30}"""], (await sent.BodiesAsync(CleanupTab.SettingsRequest)).Select(body => body.ToJsonString()));
        Assert.Equal(30, (await CleanupTab.OperatorSettingsAsync(page.Context, BaseUrl)).GetProperty("unclaimed_handback_window_days").GetInt32());
        Assert.Equal(30, (await CleanupTab.MaintenanceAsync(page.Context, BaseUrl, CleanupTab.Unclaimed)).GetProperty("window_days").GetInt32());
    }

    [E2EFact]
    public async Task Last_run_and_Next_run_say_what_the_server_holds_for_each_job()
    {
        await using var own = await E2EServer.StartWithSettingsAsync();
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await CleanupTab.OpenTabAsync(page);

        var unclaimed = CleanupTab.Row(page, CleanupTab.Unclaimed);
        await Expect(unclaimed.Locator("td[data-col='lastRun']")).ToHaveTextAsync("Not yet.");
        await Expect(unclaimed.Locator("td[data-col='nextRun']")).ToHaveTextAsync("Off");

        await Poll.UntilAsync(async () => (await RunStateAsync(page.Context, baseUrl, CleanupTab.Leftover)).Completed is not null, "the sweep Weir runs when it starts to finish");
        var leftover = await CleanupTab.MaintenanceAsync(page.Context, baseUrl, CleanupTab.Leftover);
        var completed = leftover.GetProperty("last_completed_at").GetString()!;
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='lastRun']")).ToHaveTextAsync(await AppDates.ShownAsync(page, completed, "UTC"));
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='nextRun']")).ToHaveTextAsync(await NextRunWordsAsync(page, page.Context, baseUrl, CleanupTab.Leftover));

        var group = CleanupTab.Switch(page, CleanupTab.UnclaimedName);
        await RadioIn(group, "On").ClickAsync();
        await page.GetByTestId($"processing-maintenance-confirm-{CleanupTab.Unclaimed}-enable-confirm").ClickAsync();
        await Expect(CleanupTab.Notice(page)).ToContainTextAsync("is on.");
        await Expect(unclaimed.Locator("td[data-col='nextRun']")).Not.ToHaveTextAsync("Off");
        await Expect(unclaimed.Locator("td[data-col='nextRun']")).ToHaveTextAsync(await NextRunWordsAsync(page, page.Context, baseUrl, CleanupTab.Unclaimed));
    }

    [E2EFact]
    public async Task A_reader_sees_the_jobs_with_every_control_disabled_and_no_Run_now()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        E2EDatabase.SetRole(Server.DatabasePath, Navigation.BootstrapUser, "viewer");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await CleanupTab.OpenTabAsync(page);

        await Expect(CleanupTab.Headings(page)).ToHaveTextAsync(["Job", "On", "Every", "Last run", "Next run"]);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true })).ToHaveCountAsync(0);
        foreach (var name in new[] { CleanupTab.LeftoverName, CleanupTab.UnclaimedName })
        {
            await Expect(RadioIn(CleanupTab.Switch(page, name), "On")).ToBeDisabledAsync();
            await Expect(RadioIn(CleanupTab.Switch(page, name), "Off")).ToBeDisabledAsync();
            await Expect(CleanupTab.Every(page, name)).ToBeDisabledAsync();
        }

        await Expect(CleanupTab.DaysField(page)).ToBeDisabledAsync();
        var keepCopy = page.GetByTestId("processing-maintenance-keep-failed-copy");
        await Expect(RadioIn(keepCopy.GetByRole(AriaRole.Radiogroup), "On")).ToBeDisabledAsync();
    }

    [E2EFact]
    public async Task The_headings_sort_the_jobs_with_their_settings_following_them_and_Reset_columns_puts_them_back()
    {
        await using var own = await E2EServer.StartWithSettingsAsync();
        var baseUrl = own.BaseUrl.GetLeftPart(UriPartial.Authority);
        using var admin = new WeirClient(own.BaseUrl);
        await admin.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        // The leftover files run as the server starts, a movie sweep and a TV one, so the order the headings sort by is only settled once both
        // have ended; the unclaimed copies are then run, and run again while they are not the latest.
        await Poll.UntilAsync(
            async () =>
            {
                var (unclaimed, unclaimedIdle) = await RunStateAsync(page.Context, baseUrl, CleanupTab.Unclaimed);
                var (leftover, leftoverIdle) = await RunStateAsync(page.Context, baseUrl, CleanupTab.Leftover);
                if (!unclaimedIdle || !leftoverIdle || leftover is null)
                {
                    return false;
                }

                if (unclaimed is not null && string.CompareOrdinal(unclaimed, leftover) > 0)
                {
                    return true;
                }

                var ran = await admin.PostWithCsrfAsync(Run, new JsonObject { ["family"] = CleanupTab.Unclaimed, ["media_scope"] = "movie" });
                Assert.True(ran.Status == System.Net.HttpStatusCode.OK, ran.ToString());
                return false;
            },
            "the unclaimed copies to have run after the leftover files");
        await CleanupTab.OpenTabAsync(page);

        var jobs = page.GetByTestId("processing-maintenance-section").Locator("tbody tr");
        string[] original = ["Leftover work files", "Keep a failed file", "Cleaned copies nobody picked up", "Cleaned copies nobody picked up wait"];
        await AssertOrderAsync(jobs, original);

        var on = CleanupTab.Headings(page).Filter(new() { HasText = "On" }).GetByRole(AriaRole.Button);
        await on.ClickAsync();
        await AssertOrderAsync(jobs, original);
        await Expect(CleanupTab.Headings(page).Filter(new() { HasText = "On" })).ToHaveAttributeAsync("aria-sort", "descending");
        await on.ClickAsync();
        await AssertOrderAsync(jobs, ["Cleaned copies nobody picked up", "Leftover work files", "Keep a failed file", "Cleaned copies nobody picked up wait"]);

        var lastRun = CleanupTab.Headings(page).Filter(new() { HasText = "Last run" }).GetByRole(AriaRole.Button);
        await lastRun.ClickAsync();
        await AssertOrderAsync(jobs, ["Cleaned copies nobody picked up", "Leftover work files", "Keep a failed file", "Cleaned copies nobody picked up wait"]);
        await lastRun.ClickAsync();
        await AssertOrderAsync(jobs, original);

        var every = CleanupTab.Headings(page).Filter(new() { HasText = "Every" }).GetByRole(AriaRole.Button);
        await every.ClickAsync();
        await AssertOrderAsync(jobs, original);
        await every.ClickAsync();
        await AssertOrderAsync(jobs, ["Cleaned copies nobody picked up", "Leftover work files", "Keep a failed file", "Cleaned copies nobody picked up wait"]);

        var nextRun = CleanupTab.Headings(page).Filter(new() { HasText = "Next run" }).GetByRole(AriaRole.Button);
        await nextRun.ClickAsync();
        await AssertOrderAsync(jobs, original);
        await nextRun.ClickAsync();
        await AssertOrderAsync(jobs, original);

        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns", Exact = true }).ClickAsync();
        await Expect(CleanupTab.Headings(page).Locator("[aria-sort]")).ToHaveCountAsync(0);
        await every.ClickAsync();
        await every.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns", Exact = true }).ClickAsync();
        await AssertOrderAsync(jobs, original);
    }

    [E2EFact]
    public async Task A_column_moves_from_the_keyboard_and_goes_back_with_Reset_columns()
    {
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        await Expect(CleanupTab.Headings(page)).ToHaveTextAsync(["Job", "On", "Every", "Last run", "Next run", "Run now"]);

        await CleanupTab.Headings(page).Filter(new() { HasText = "Every" }).GetByRole(AriaRole.Button).FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowRight");
        await Expect(CleanupTab.Headings(page)).ToHaveTextAsync(["Job", "On", "Last run", "Every", "Next run", "Run now"]);
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td").Nth(1)).ToHaveAttributeAsync("data-col", "lastRun");

        await page.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns", Exact = true }).ClickAsync();
        await Expect(CleanupTab.Headings(page)).ToHaveTextAsync(["Job", "On", "Every", "Last run", "Next run", "Run now"]);
    }

    [E2EFact]
    public async Task A_change_made_elsewhere_shows_in_the_open_table_without_a_reload()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await CleanupTab.OpenAsync(page, BaseUrl);
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var group = CleanupTab.Switch(page, CleanupTab.LeftoverName);
        await Expect(RadioIn(group, "On")).ToHaveAttributeAsync("aria-checked", "true");

        var off = await admin.PutWithCsrfAsync(Settings, new JsonObject { ["work_temp_stale_sweep_enabled"] = false, ["work_temp_stale_sweep_interval_seconds"] = 43200 });
        Assert.True(off.Status == System.Net.HttpStatusCode.OK, off.ToString());

        await Expect(RadioIn(group, "Off")).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(CleanupTab.Row(page, CleanupTab.Leftover).Locator("td[data-col='nextRun']")).ToHaveTextAsync("Off");
        await Expect(CleanupTab.Every(page, CleanupTab.LeftoverName)).ToHaveValueAsync("43200");
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }

    private static async Task AssertOrderAsync(ILocator rows, string[] startsWith)
    {
        await Expect(rows).ToHaveCountAsync(startsWith.Length);
        for (var index = 0; index < startsWith.Length; index++)
        {
            await Expect(rows.Nth(index)).ToContainTextAsync(startsWith[index]);
        }
    }
}
