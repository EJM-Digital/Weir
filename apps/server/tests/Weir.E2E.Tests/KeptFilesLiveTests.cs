using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Activity's Kept list follows files being kept, with no reload.</summary>
public sealed class KeptFilesLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    [E2EFact]
    public async Task A_file_kept_through_the_api_appears_in_the_Kept_list_without_a_reload()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        await File.WriteAllBytesAsync(Path.Join(watched, "Harbour Lights 2024.mkv"), [1, 2, 3, 4, 5]);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: true);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E kept films", watched, output);
        try
        {
            var fileId = E2EDatabase.InsertFailedFile(Server.DatabasePath, id, "Harbour Lights 2024.mkv", sizeBytes: 5);
            await page.GotoAsync($"{BaseUrl}/activity?show=kept");
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            await Expect(page.GetByText("No files are kept right now.")).ToBeVisibleAsync(new() { Timeout = PushMs });

            await WeirApi.KeepFileAsync(page.Context, BaseUrl, fileId);

            await Expect(page.GetByTestId("kept-files-table")).ToContainTextAsync("Harbour Lights 2024.mkv", new() { Timeout = PushMs });
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
            await WeirApi.SetPausedAsync(page.Context, BaseUrl, paused: false);
        }
    }
}
