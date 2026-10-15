using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Activity follows Weir as it changes, with no reload: every change is made through Weir's API, as a media manager makes it.</summary>
public sealed class LiveActivityTests(E2EServer server) : E2ETestBase(server)
{
    // Long enough for the stream's own safety net to be no part of it: the page is told the moment the change commits.
    private const float PushMs = 10_000;

    private const string FirstFile = "Harbour Lights 2024.mkv";
    private const string SecondFile = "Paper Lanterns 2023.mkv";

    private static ILocator Row(IPage page, string fileName) =>
        page.GetByRole(AriaRole.Row).Filter(new() { HasText = fileName });

    [E2EFact]
    public async Task A_hand_off_finishing_changes_its_row_in_place_and_a_new_one_adds_a_row_without_a_reload()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await NewPageAsync();
        await page.RunAndWaitForResponseAsync(
            () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
            response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
        await Navigation.OpenSidebarAsync(page, "Activity");
        await Expect(page.GetByTestId("activity-page")).ToBeVisibleAsync();
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        await Expect(page.GetByText("Nothing yet.", new() { Exact = false })).ToBeVisibleAsync();

        await rig.HandOffAsync("h-one", FirstFile);

        var first = Row(page, FirstFile);
        await rig.WaitForWritesStartedAsync(1);
        await Expect(first).ToContainTextAsync("Writing", new() { Timeout = PushMs });

        rig.ReleasePass(FirstFile);
        await rig.WaitForWorkToEndAsync();

        await Expect(first).ToContainTextAsync("Done", new() { Timeout = PushMs });
        await Expect(first).Not.ToContainTextAsync("Writing");

        await rig.WaitForReportAsync();
        await rig.ReportImportedAsync("h-one");

        await Expect(first).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });

        await rig.HandOffAsync("h-two", SecondFile);

        var second = Row(page, SecondFile);
        await rig.WaitForWritesStartedAsync(2);
        await Expect(second).ToContainTextAsync("Writing", new() { Timeout = PushMs });
        await Expect(page.GetByRole(AriaRole.Row).Nth(1)).ToContainTextAsync(SecondFile);
        await Expect(first).ToContainTextAsync("Imported by Deluno");
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");

        rig.ReleasePass(SecondFile);
        await rig.WaitForWorkToEndAsync();
        await Expect(second).ToContainTextAsync("Done", new() { Timeout = PushMs });
    }
}
