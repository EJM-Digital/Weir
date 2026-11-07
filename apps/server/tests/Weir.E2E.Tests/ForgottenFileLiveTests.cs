using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>A file taken off Activity's list, whose copy Deluno imported, is cleaned again when Deluno hands it over again (#1401): the new copy reads Done until Deluno answers for it.</summary>
public sealed class ForgottenFileLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const string FileName = "Pulp Fiction 1994.mkv";

    [E2EFact]
    public async Task A_forgotten_file_handed_over_again_after_it_was_imported_is_cleaned_again_and_reads_done()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var context = await NewContextAsync(new ViewportSize { Width = 1280, Height = 720 });
        var activity = await context.NewPageAsync();
        ApplyDefaultTimeout(activity);
        await Navigation.EnsureSignedInAsync(activity, rig.BaseUrl);
        await Navigation.OpenSidebarAsync(activity, "Activity");
        var row = activity.GetByRole(AriaRole.Row).Filter(new() { HasText = FileName });
        await rig.HandOffRootFileAsync("h-root-1", FileName);
        rig.ReleasePass(FileName);
        await rig.WaitForWorkToEndAsync();
        await Expect(row).ToContainTextAsync("Done", new() { Timeout = PushMs });
        await rig.WaitForReportAsync();
        await rig.ReportImportedAsync("h-root-1");
        await Expect(row).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });

        await rig.ForgetAsync(FileName);
        await Expect(row).ToHaveCountAsync(0, new() { Timeout = PushMs });

        await rig.HandOffRootFileAgainAsync("h-root-2", FileName);

        await rig.WaitForWorkToEndAsync();
        await Expect(row).ToContainTextAsync("Done", new() { Timeout = PushMs });
        await Expect(row).Not.ToContainTextAsync("Imported by Deluno");
        await Expect(row).Not.ToContainTextAsync("Waiting");
        Assert.Equal(2, rig.RemuxCount);
    }
}
