using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup › Workflows › Schedule, the hours editor: opening and closing it, drawing a week with the pointer and the keyboard, and what Save sends and keeps.</summary>
public sealed class ScheduleEditorTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E Draw hours";

    [E2EFact]
    public async Task Change_hours_opens_and_closes_the_editor_and_Save_sends_the_drawn_week_keeping_every_other_setting()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await ScheduleTabTests.CreateAsync(admin, Name, string.Empty);
        var other = LibraryBodies.Unchanged(await WorkflowEditor.ReadAsync(admin, id));
        other["priority"] = 7;
        other["max_attempts"] = 4;
        Assert.True((await admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{id}", other)).Status == System.Net.HttpStatusCode.OK);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var log = RequestLog.Watch(page);
            var change = ScheduleTabTests.Row(page, Name).GetByRole(AriaRole.Button, new() { Name = "Change hours" });
            var editor = page.GetByTestId("schedule-library-editor");
            var save = page.GetByTestId("schedule-library-save");
            await Expect(change).ToHaveAttributeAsync("aria-expanded", "false");

            // The same button closes it, and so does Close.
            await change.ClickAsync();
            await Expect(change).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(editor).ToBeVisibleAsync();
            await Expect(editor).ToContainTextAsync($"{Name}: when it may start work");
            await Expect(save).ToBeDisabledAsync();
            await change.ClickAsync();
            await Expect(editor).ToHaveCountAsync(0);
            await Expect(change).ToHaveAttributeAsync("aria-expanded", "false");
            await change.ClickAsync();
            await editor.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Expect(editor).ToHaveCountAsync(0);

            // A click, the keyboard and a drag across the hours each switch hours off.
            await change.ClickAsync();
            await Cell(page, 0, 3).ClickAsync();
            await Expect(Cell(page, 0, 3)).ToHaveAttributeAsync("aria-pressed", "false");
            await Expect(Cell(page, 0, 3)).ToHaveAttributeAsync("aria-label", "Monday 03:00, off");
            await Cell(page, 0, 4).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Expect(Cell(page, 0, 4)).ToHaveAttributeAsync("aria-pressed", "false");
            await page.Keyboard.PressAsync("ArrowRight");
            await Expect(Cell(page, 0, 5)).ToBeFocusedAsync();
            await DragAcrossAsync(page, 0, 8, 10);
            foreach (var hour in new[] { 8, 9, 10 })
            {
                await Expect(Cell(page, 0, hour)).ToHaveAttributeAsync("aria-pressed", "false");
            }

            var mark = log.Mark();
            await Expect(save).ToHaveTextAsync($"Save {Name}'s hours");
            await save.ClickAsync();
            await Expect(editor).ToHaveCountAsync(0);

            var expected = new string([.. Enumerable.Range(0, WeekGrids.SlotsPerWeek).Select(slot => Array.IndexOf(new[] { 3, 4, 8, 9, 10 }, slot / WeekGrids.SlotsPerHour) >= 0 ? '0' : '1')]);
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"PUT {WeirClient.Api}/processing/libraries/{id}", $"{sent.Method} {sent.PathAndQuery}");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.Equal(expected, (string)body["schedule_grid"]!);
            Assert.True((bool)body["schedule_enabled"]!);
            Assert.False((bool)body["schedule_hours_limited"]!);
            var stored = await WorkflowEditor.ReadAsync(admin, id);
            Assert.Equal(expected, (string)stored["schedule_grid"]!);
            foreach (var (field, value) in other.Where(field => field.Key is not ("schedule_grid" or "schedule_enabled" or "schedule_hours_limited" or "csrf_token")))
            {
                Assert.True(JsonNode.DeepEquals(value, body[field]), $"{field} was sent as {body[field]?.ToJsonString()}, not {value?.ToJsonString()}");
                Assert.True(JsonNode.DeepEquals(value, stored[field]), $"{field} is stored as {stored[field]?.ToJsonString()}, not {value?.ToJsonString()}");
            }

            await Expect(ScheduleTabTests.Row(page, Name).Locator(".mm-week__hour.is-on")).ToHaveCountAsync((7 * 24) - 5);

            // Clear all is a week with no hours, Any time is no restriction.
            await change.ClickAsync();
            await editor.GetByTestId("schedule-none").ClickAsync();
            await save.ClickAsync();
            await Expect(ScheduleTabTests.RightNow(page, Name)).ToHaveTextAsync("Never: no hours are chosen");
            Assert.Equal(WeekGrids.None(), (string)(await WorkflowEditor.ReadAsync(admin, id))["schedule_grid"]!);
            await change.ClickAsync();
            await editor.GetByTestId("schedule-clear").ClickAsync();
            await save.ClickAsync();
            await Expect(ScheduleTabTests.RightNow(page, Name)).ToHaveTextAsync("Any time");
            Assert.Equal(string.Empty, (string)(await WorkflowEditor.ReadAsync(admin, id))["schedule_grid"]!);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task Closing_or_switching_with_hours_drawn_and_not_saved_asks_first_and_Escape_keeps_editing()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var id = await ScheduleTabTests.CreateAsync(admin, Name, string.Empty);
        var otherId = await ScheduleTabTests.CreateAsync(admin, "E2E Other hours", string.Empty);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var log = RequestLog.Watch(page);
            var mark = log.Mark();
            var editor = page.GetByTestId("schedule-library-editor");
            var dialog = page.GetByTestId("settings-unsaved-changes");
            var question = $"You have unsaved changes to {Name}'s hours. Leave without saving?";
            await ScheduleTabTests.Row(page, Name).GetByRole(AriaRole.Button, new() { Name = "Change hours" }).ClickAsync();
            await Cell(page, 2, 6).ClickAsync();

            // Close asks; Escape and Keep editing both stay, with the drawn hour still off.
            await editor.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Expect(dialog).ToContainTextAsync(question);
            await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(Cell(page, 2, 6)).ToHaveAttributeAsync("aria-pressed", "false");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep editing" }).ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(editor).ToBeVisibleAsync();

            // Another workflow's hours asks too, and Leave without saving moves on with the first one's edit dropped.
            await ScheduleTabTests.Row(page, "E2E Other hours").GetByRole(AriaRole.Button, new() { Name = "Change hours" }).ClickAsync();
            await Expect(dialog).ToContainTextAsync(question);
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Leave without saving" }).ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(editor).ToContainTextAsync("E2E Other hours: when it may start work");
            await Expect(Cell(page, 2, 6)).ToHaveAttributeAsync("aria-pressed", "true");

            log.AssertNothingSentSince(mark);
            Assert.Equal(string.Empty, (string)(await WorkflowEditor.ReadAsync(admin, id))["schedule_grid"]!);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, otherId);
        }
    }

    private static ILocator Cell(IPage page, int day, int hour) => page.GetByTestId($"schedule-cell-{day}-{hour}");

    /// <summary>Presses on one hour and drags across the others on the same day, switching each off as the pointer reaches it.</summary>
    private static async Task DragAcrossAsync(IPage page, int day, int fromHour, int toHour)
    {
        var from = (await Cell(page, day, fromHour).BoundingBoxAsync())!;
        var to = (await Cell(page, day, toHour).BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(from.X + (from.Width / 2), from.Y + (from.Height / 2));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(to.X + (to.Width / 2), to.Y + (to.Height / 2), new() { Steps = 10 });
        await page.Mouse.UpAsync();
    }
}
