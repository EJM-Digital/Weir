using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Workflows › Schedule, the table: each workflow's week, whether it may start work right now, how it follows the clock and the server, and what a viewer can do.</summary>
public sealed class ScheduleTabTests(E2EServer server) : E2ETestBase(server)
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const int HoursAhead = 12;

    [E2EFact]
    public async Task Right_now_says_any_time_never_open_until_or_closed_opens_and_the_clock_moves_it_on()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var target = DateTime.UtcNow.AddHours(HoursAhead);
        var (day, hour) = WeekGrids.Of(target);
        var halfPastTheHour = target.Date.AddHours(target.Hour).AddMinutes(30);
        var any = await CreateAsync(admin, "E2E Any time", string.Empty);
        var never = await CreateAsync(admin, "E2E Never", WeekGrids.None());
        var open = await CreateAsync(admin, "E2E Open", WeekGrids.AllExcept(day, hour));
        var closed = await CreateAsync(admin, "E2E Closed", WeekGrids.OnlyAt(day, hour));
        var off = await CreateAsync(admin, "E2E Off", string.Empty, enabled: false);
        var page = await NewPageAsync();
        await page.Clock.InstallAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var until = $"until {WeekGrids.Words(day, hour)}";
            var opens = $"opens {WeekGrids.Words(day, hour)}";

            await Expect(RightNow(page, "E2E Any time")).ToHaveTextAsync("Any time");
            await Expect(RightNow(page, "E2E Never")).ToHaveTextAsync("Never: no hours are chosen");
            await Expect(RightNow(page, "E2E Open")).ToContainTextAsync("Open");
            await Expect(RightNow(page, "E2E Open")).ToContainTextAsync(until);
            await Expect(RightNow(page, "E2E Closed")).ToContainTextAsync("Closed");
            await Expect(RightNow(page, "E2E Closed")).ToContainTextAsync(opens);
            await Expect(Row(page, "E2E Off")).ToContainTextAsync("Switched off in Workflows");
            await Expect(Row(page, "E2E Any time").Locator("td[data-label='Looks for new files']")).ToContainTextAsync(new Regex("^Every "));

            // Each week shows its hours: all of them, none, or all but one.
            await Expect(Row(page, "E2E Any time").Locator(".mm-week")).ToHaveAttributeAsync("aria-label", "E2E Any time: when it may start work, Monday to Sunday");
            await Expect(Row(page, "E2E Never").Locator(".mm-week__hour.is-on")).ToHaveCountAsync(0);
            await Expect(Row(page, "E2E Open").Locator(".mm-week__hour.is-on")).ToHaveCountAsync((7 * 24) - 1);
            await Expect(Row(page, "E2E Closed").Locator(".mm-week__hour.is-on")).ToHaveCountAsync(1);

            // Half way through the one hour that differs the open week has closed, once the clock has ticked (it ticks every 30 seconds).
            await page.Clock.FastForwardAsync((long)(halfPastTheHour - DateTime.UtcNow).TotalMilliseconds);
            await page.Clock.RunForAsync(31_000);
            await Expect(RightNow(page, "E2E Open")).ToContainTextAsync("Closed");
            await Expect(RightNow(page, "E2E Open")).ToContainTextAsync($"opens {WeekGrids.Words(hour == 23 ? (day + 1) % 7 : day, (hour + 1) % 24)}");
            await Expect(RightNow(page, "E2E Closed")).ToContainTextAsync("Open");
        }
        finally
        {
            foreach (var id in new[] { any, never, open, closed, off })
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            }
        }
    }

    [E2EFact]
    public async Task The_week_and_the_next_look_follow_the_server_without_a_reload()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await CreateAsync(admin, "E2E Live week", string.Empty);
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            await Expect(RightNow(page, "E2E Live week")).ToHaveTextAsync("Any time");
            var lookAt = Row(page, "E2E Live week").Locator("td[data-label='Looks for new files']");
            await Expect(lookAt).ToHaveTextAsync("Every 5 minutes");

            var stored = await WorkflowEditor.ReadAsync(admin, id);
            var body = LibraryBodies.Unchanged(stored);
            body["schedule_grid"] = WeekGrids.None();
            body["scan_interval_seconds"] = 3600;
            var saved = await admin.PutWithCsrfAsync($"{Libraries}/{id}", body);
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());

            await Expect(RightNow(page, "E2E Live week")).ToHaveTextAsync("Never: no hours are chosen");
            await Expect(lookAt).ToHaveTextAsync("Every hour");
            await Expect(Row(page, "E2E Live week").Locator(".mm-week__hour.is-on")).ToHaveCountAsync(0);
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task A_viewer_can_open_the_hours_to_read_them_but_not_save_them_or_start_a_scan()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        using var folders = new TemporaryFolder();
        var (day, hour) = WeekGrids.Of(DateTime.UtcNow.AddHours(HoursAhead));
        var id = await CreateAsync(admin, "E2E Viewer hours", WeekGrids.AllExcept(day, hour), Directory.CreateDirectory(Path.Join(folders.Path, "in")).FullName);
        var adminPage = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(adminPage, BaseUrl);
        try
        {
            var page = await NewPageAsync();
            await ViewerAccount.SignInAsync(page, BaseUrl, Server.DatabasePath);
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var row = Row(page, "E2E Viewer hours");
            await Expect(row).ToBeVisibleAsync();

            await row.GetByRole(AriaRole.Button, new() { Name = "Change hours" }).ClickAsync();
            var editor = page.GetByTestId("schedule-library-editor");
            await Expect(editor).ToBeVisibleAsync();
            await Expect(editor.GetByTestId($"schedule-cell-{day}-{hour}")).ToBeDisabledAsync();
            await Expect(editor.GetByTestId("schedule-clear")).ToBeDisabledAsync();
            await Expect(editor.GetByTestId("schedule-none")).ToBeDisabledAsync();
            await Expect(page.GetByTestId("schedule-library-save")).ToBeDisabledAsync();
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Scan E2E Viewer hours now" })).ToBeDisabledAsync();
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(adminPage.Context, BaseUrl, id);
        }
    }

    internal static ILocator Row(IPage page, string name) => page.GetByTestId("schedule-library-row").Filter(new() { Has = page.GetByRole(AriaRole.Rowheader, new() { Name = name }) });

    internal static ILocator RightNow(IPage page, string name) => Row(page, name).Locator("td[data-label='Right now']");

    internal static async Task<long> CreateAsync(WeirClient admin, string name, string grid, string watched = "", bool enabled = true)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["media_type"] = "movie",
            ["schedule_grid"] = grid,
            ["watched_folder"] = watched,
            ["output_folder"] = watched.Length == 0 ? string.Empty : Directory.CreateDirectory(Path.Join(Path.GetDirectoryName(watched)!, "out")).FullName,
            ["enabled"] = enabled,
        };
        var created = await admin.PostWithCsrfAsync(Libraries, body);
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }
}
