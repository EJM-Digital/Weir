using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>Performance › Cleanup as a person uses it: the two jobs, their switches, lists and buttons, and what the server holds for them.</summary>
public static class CleanupTab
{
    public const string Leftover = "work_temp_stale_sweep";
    public const string Unclaimed = "unclaimed_handbacks";
    public const string LeftoverName = "Leftover work files";
    public const string UnclaimedName = "Cleaned copies nobody picked up";
    public const string SettingsRequest = "PUT /api/v1/processing/operator-settings";
    public const string RunRequest = "POST /api/v1/processing/maintenance/run";

    /// <summary>Signs in, opens the tab and waits until its jobs are on screen.</summary>
    public static async Task<IPage> OpenAsync(IPage page, string baseUrl)
    {
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await OpenTabAsync(page);
        return page;
    }

    public static async Task OpenTabAsync(IPage page)
    {
        await Navigation.OpenTabAsync(page, "Performance", "Cleanup");
        await Expect(Row(page, Leftover)).ToBeVisibleAsync();
    }

    public static ILocator Row(IPage page, string family) => page.GetByTestId($"processing-maintenance-{family}");

    public static ILocator Notice(IPage page) => page.GetByTestId("processing-maintenance-notice");

    public static ILocator Switch(IPage page, string jobName) =>
        page.GetByRole(AriaRole.Radiogroup, new() { Name = $"{jobName} on", Exact = true });

    public static ILocator Every(IPage page, string jobName) =>
        page.GetByRole(AriaRole.Combobox, new() { Name = $"How often {jobName} runs", Exact = true });

    public static ILocator DaysField(IPage page) =>
        page.GetByRole(AriaRole.Spinbutton, new() { Name = "Cleaned copies nobody picked up wait for", Exact = true });

    public static ILocator RunNow(IPage page, string family) => page.GetByTestId($"processing-maintenance-run-{family}");

    public static ILocator Headings(IPage page) => page.GetByTestId("processing-maintenance-section").Locator("thead th");

    public static async Task<JsonElement> MaintenanceAsync(IBrowserContext context, string baseUrl, string family)
    {
        var state = await PageApi.GetAsync(context, baseUrl, "/api/v1/processing/maintenance");
        return state.GetProperty("families").EnumerateArray().Single(job => job.GetProperty("family").GetString() == family);
    }

    public static Task<JsonElement> OperatorSettingsAsync(IBrowserContext context, string baseUrl) =>
        PageApi.GetAsync(context, baseUrl, "/api/v1/processing/operator-settings");
}
