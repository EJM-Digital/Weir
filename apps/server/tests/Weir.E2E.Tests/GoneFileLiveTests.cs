using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>A file that leaves the watched folder before Weir starts on it stops saying it is waiting, with no reload.</summary>
public sealed class GoneFileLiveTests(E2EServer server) : E2ETestBase(server)
{
    // A hang guard, not a pace: a scan has to run, and then look again a few seconds later before it believes the file is gone, and
    // the wait ends the moment the page shows it.
    private const float ScanMs = 45_000;

    [E2EFact]
    public async Task A_file_deleted_while_it_waits_to_settle_loses_its_waiting_label_without_a_reload()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        var file = Path.Join(watched, "Harbour Lights 2024.mkv");
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var context = page.Context;
        var id = await WeirWorkflowApi.CreateAsync(context, BaseUrl, "E2E settling films", watched, output);
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5]);
            await page.GotoAsync(BaseUrl);
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            await WeirWorkflowApi.StartWatchedFolderScanAsync(context, BaseUrl, id);
            var card = page.GetByRole(AriaRole.Button, new() { Name = "Harbour Lights (2024):" });
            await Expect(card).ToHaveAccessibleNameAsync(new Regex("Waiting to settle"), new() { Timeout = ScanMs });

            File.Delete(file);
            await WeirWorkflowApi.StartWatchedFolderScanAsync(context, BaseUrl, id);

            await Expect(card).ToHaveAccessibleNameAsync(new Regex("No longer there"), new() { Timeout = ScanMs });
            await Expect(card).Not.ToHaveAccessibleNameAsync(new Regex("Waiting to settle"));
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(context, BaseUrl, id);
        }
    }
}
