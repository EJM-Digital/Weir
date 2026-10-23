using System.Text;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Backups › Restore from file…, which opens the computer's own file chooser: a file that is not a Weir export is refused in plain words and nothing is restored.</summary>
public sealed class RestoreFromFileTests(E2EServer server) : E2ETestBase(server)
{
    private static async Task ChooseAsync(IPage page, string name, string content)
    {
        var chooser = await page.RunAndWaitForFileChooserAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Restore from file…", Exact = true }).ClickAsync());
        await chooser.SetFilesAsync(new FilePayload { Name = name, MimeType = "application/json", Buffer = Encoding.UTF8.GetBytes(content) });
    }

    [E2EFact]
    public async Task A_file_that_is_not_a_Weir_export_is_refused_and_no_restore_dialog_opens()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var (page, requests) = (system.Page, system.Requests);
        var mark = requests.Mark();

        await ChooseAsync(page, "notes.json", "null");
        await Expect(page.GetByText("This file is not valid JSON.", new() { Exact = true })).ToBeVisibleAsync();
        await ChooseAsync(page, "other.json", """{"hello":"world"}""");

        await Expect(page.GetByText("This file is not a Weir configuration export.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("restore-configuration-dialog")).ToHaveCountAsync(0);
        requests.AssertNothingSentSince(mark);
    }
}
