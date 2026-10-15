using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The controls on a workflow's own row: the On / Off switch under Setup › Workflows › File paths, which saves at once, and Scan now under
/// Schedule, which asks Weir to look at the workflow's watched folder.
/// </summary>
public sealed class WorkflowRowControlsTests(E2EServer server) : E2ETestBase(server)
{
    // A hang guard, not a pace: a scan has to run before the dashboard has anything to show, and the wait ends the moment the page shows it.
    private const float ScanMs = 45_000;

    [E2EFact]
    public async Task The_On_Off_switch_turns_a_workflow_off_and_on_and_the_server_holds_each_change()
    {
        const string name = "E2E switched films";
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(
            page.Context,
            BaseUrl,
            name,
            Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName,
            Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName);
        try
        {
            await Navigation.OpenTabAsync(page, "Workflows", "File paths");
            var enabled = page.GetByTestId($"processing-library-{id}").GetByRole(AriaRole.Radiogroup, new() { Name = $"{name} enabled" });
            var on = enabled.GetByRole(AriaRole.Radio, new() { Name = "On", Exact = true });
            var off = enabled.GetByRole(AriaRole.Radio, new() { Name = "Off", Exact = true });
            await Expect(on).ToHaveAttributeAsync("aria-checked", "true");
            Assert.True(await IsEnabledAsync(admin, id));

            await off.ClickAsync();

            // The switch shows what the server holds, so it moves once the change is saved.
            await Expect(off).ToHaveAttributeAsync("aria-checked", "true");
            Assert.False(await IsEnabledAsync(admin, id));

            await on.ClickAsync();

            await Expect(on).ToHaveAttributeAsync("aria-checked", "true");
            Assert.True(await IsEnabledAsync(admin, id));
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task Scan_now_makes_Weir_look_at_the_watched_folder_and_the_dashboard_shows_the_file_it_found()
    {
        const string name = "E2E scanned on demand";
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, name, watched, output);
        try
        {
            await File.WriteAllBytesAsync(Path.Join(watched, "Lantern Quay 2024.mkv"), [1, 2, 3, 4, 5]);
            await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
            var scan = page.GetByRole(AriaRole.Button, new() { Name = $"Scan {name} now" });
            await Expect(scan).ToContainTextAsync("Scan now");

            await scan.ClickAsync();

            await Expect(scan).ToContainTextAsync("Scan queued", new() { Timeout = ScanMs });
            await Navigation.OpenSidebarAsync(page, "Dashboard");
            var card = page.GetByRole(AriaRole.Button, new() { Name = "Lantern Quay (2024):" });
            await Expect(card).ToHaveAccessibleNameAsync(new Regex("Waiting to settle"), new() { Timeout = ScanMs });
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    private static async Task<bool> IsEnabledAsync(WeirClient admin, long id)
    {
        var read = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{id}");
        Assert.Equal(HttpStatusCode.OK, read.Status);
        return (bool)read.Fields["enabled"]!;
    }
}
