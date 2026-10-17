using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Setup at the width of a phone: the workflow list, the editor with every group open, the hours editor and the profiles never need the page to scroll sideways.</summary>
public sealed class SetupPhoneWidthTests(E2EServer server) : E2ETestBase(server)
{
    private const int Width = 390;

    [E2EFact]
    public async Task The_workflow_list_the_editor_the_hours_editor_and_the_profiles_fit_a_phone_without_sideways_scrolling()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E phone films", string.Empty, string.Empty);
        try
        {
            await page.SetViewportSizeAsync(Width, 844);
            await page.GotoAsync($"{BaseUrl}/setup/workflows", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(WorkflowEditor.Row(page, id)).ToContainTextAsync("E2E phone films");
            await AssertFitsAsync(page, "the workflow list");

            var form = await WorkflowEditor.OpenEditAsync(page, id);
            foreach (var group in new[] { "Media manager", "Folder chain", "Intake rules", "File readiness", "Output safety", "Capacity", "When a file fails", "Advanced", "When this workflow may run" })
            {
                await WorkflowEditor.OpenGroupAsync(form, group);
            }

            await AssertFitsAsync(page, "the workflow editor");
            await form.EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            var panel = (await form.BoundingBoxAsync())!;
            Assert.True(panel.X >= 0 && panel.X + panel.Width <= Width, $"The editor spans {panel.X} to {panel.X + panel.Width} on a screen {Width} wide.");
            Assert.True(await form.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth"), "The editor scrolls sideways.");
            await page.Keyboard.PressAsync("Escape");
            await Expect(form).ToHaveCountAsync(0);

            await page.GotoAsync($"{BaseUrl}/setup/workflows/schedule", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ScheduleTabTests.Row(page, "E2E phone films").GetByRole(AriaRole.Button, new() { Name = "Change hours" }).ClickAsync();
            await Expect(page.GetByTestId("schedule-library-editor")).ToBeVisibleAsync();
            await AssertFitsAsync(page, "the hours editor");

            await page.GotoAsync($"{BaseUrl}/setup/rules", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Profiles", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
            await AssertFitsAsync(page, "the profiles");
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    private static async Task AssertFitsAsync(IPage page, string what) =>
        Assert.True(
            await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"),
            $"The page scrolls sideways at {Width} wide with {what} showing.");
}
