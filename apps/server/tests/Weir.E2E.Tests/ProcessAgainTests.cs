using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>"Process again" on a file Weir already cleaned says what it did, and the skip joins the open Dashboard's Activity stream with no reload.</summary>
public sealed class ProcessAgainTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float FinishMs = 30_000;
    private const string FileName = "Retryfilm 2017.mkv";

    [E2EFact]
    public async Task Process_again_on_an_unchanged_cleaned_file_is_skipped_visibly_and_writes_no_second_output()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var context = await NewContextAsync(new ViewportSize { Width = 1280, Height = 720 });
        var activity = await context.NewPageAsync();
        ApplyDefaultTimeout(activity);
        await Navigation.EnsureSignedInAsync(activity, rig.BaseUrl);
        await Navigation.OpenSidebarAsync(activity, "Activity");
        var row = activity.GetByRole(AriaRole.Row).Filter(new() { HasText = FileName });
        await rig.HandOffAsync("h-retry", FileName);
        rig.ReleasePass(FileName);
        await Expect(row).ToContainTextAsync("Done", new() { Timeout = FinishMs });
        await rig.WaitForReportAsync();
        await rig.ReportImportedAsync("h-retry");
        await Expect(row).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });
        Assert.Equal(1, rig.RemuxCount);

        var dashboard = await context.NewPageAsync();
        ApplyDefaultTimeout(dashboard);
        await Navigation.EnsureSignedInAsync(dashboard, rig.BaseUrl);
        await dashboard.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
        var skipped = dashboard.GetByText($"Skipped: already imported ({FileName})", new() { Exact = true });
        await Expect(skipped).ToHaveCountAsync(0);

        await row.ClickAsync();
        var actions = activity.GetByTestId("activity-file-actions");
        await actions.GetByRole(AriaRole.Button, new() { Name = "Process again", Exact = true }).ClickAsync();

        await Expect(actions.GetByRole(AriaRole.Status)).ToContainTextAsync("Weir already cleaned this file, so it skipped it.", new() { Timeout = PushMs });
        await Expect(skipped).ToBeVisibleAsync(new() { Timeout = PushMs });
        Assert.True(await dashboard.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        Assert.Equal(1, rig.RemuxCount);
        await Expect(row).ToContainTextAsync("Imported by Deluno");
    }
}
