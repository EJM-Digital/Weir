using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Activity page's own controls in a real browser, against files a real server has worked on: Try again on a file that failed, the
/// kind-of-file chips, and the headings that sort the list. What the page does is read back from the server's own answers.
/// </summary>
public sealed partial class ActivityControlsTests(E2EServer server) : E2ETestBase(server)
{
    private const float PassMs = 180_000;
    private const float PushMs = 10_000;
    private static readonly ViewportSize Desktop = new() { Width = 1600, Height = 900 };

    private const string Film = "Nosferatu 1922.mkv";
    private const string Extra = "Gallery.mkv";
    private const string Finished = "Okfilm 2017.mkv";

    [GeneratedRegex(@"(Nosferatu 1922|Okfilm 2017)\.mkv")]
    private static partial Regex ListedFile();

    [E2EFact]
    public async Task Try_again_on_a_failed_file_asks_the_server_once_and_the_file_is_cleaned_and_listed_as_done()
    {
        await using var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true);
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync(Desktop);
            var log = RequestLog.Watch(page);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await Navigation.OpenSidebarAsync(page, "Activity");
            var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = Film });
            await rig.HandOffReleaseAsync("h-retry", Film, Extra, failFirstPass: true);
            await Expect(row).ToContainTextAsync("Failed", new() { Timeout = PassMs });
            await row.ClickAsync();
            var actions = page.GetByTestId("activity-file-actions");
            var mark = log.Mark();

            await actions.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();

            await Expect(actions.GetByRole(AriaRole.Status)).Not.ToBeEmptyAsync(new() { Timeout = PushMs });
            await Expect(row).ToContainTextAsync("Done", new() { Timeout = PassMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.Matches(@"^/api/v1/processing/files/\d+/requeue$", sent.PathAndQuery);
            Assert.Equal(2, rig.RemuxCount);
            var listed = await rig.ListedFileAsync(Film);
            Assert.NotNull(listed);
            Assert.Equal("processed", (string)listed["status"]!);
        });
    }

    [E2EFact]
    public async Task A_kind_of_file_chip_lists_only_its_files_and_puts_the_choice_in_the_address()
    {
        await using var rig = await StartRigWithAFailedAndAFinishedFileAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenActivityAsync(rig);
            var group = page.GetByRole(AriaRole.Group, new() { Name = "Show" });

            await group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Failed") }).ClickAsync();

            await ExpectListedAsync(page, Film);
            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=failed"), new() { Timeout = PushMs });
            await Expect(group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Failed") })).ToHaveAttributeAsync("aria-pressed", "true");
            Assert.Equal("processing_failed", (string)(await rig.ListedFileAsync(Film))!["status"]!);

            await group.GetByRole(AriaRole.Button, new() { NameRegex = new("^Finished") }).ClickAsync();

            await ExpectListedAsync(page, Finished);
            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]show=finished"), new() { Timeout = PushMs });

            await group.GetByRole(AriaRole.Button, new() { NameRegex = new("^All") }).ClickAsync();

            // Newest first: the failed film was handed over after the finished one.
            await ExpectListedAsync(page, Film, Finished);
            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]show="), new() { Timeout = PushMs });
        });
    }

    [E2EFact]
    public async Task The_File_heading_sorts_the_list_and_a_second_click_reverses_it_in_the_order_the_server_gives()
    {
        await using var rig = await StartRigWithAFailedAndAFinishedFileAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenActivityAsync(rig);
            var file = page.GetByRole(AriaRole.Columnheader, new() { Name = "File", Exact = true });

            await file.GetByRole(AriaRole.Button).ClickAsync();

            await Expect(file).ToHaveAttributeAsync("aria-sort", "ascending", new() { Timeout = PushMs });
            Assert.Equal([Film, Finished], await ServerOrderAsync(rig, "asc"));
            await ExpectListedAsync(page, Film, Finished);

            await file.GetByRole(AriaRole.Button).ClickAsync();

            await Expect(file).ToHaveAttributeAsync("aria-sort", "descending", new() { Timeout = PushMs });
            Assert.Equal([Finished, Film], await ServerOrderAsync(rig, "desc"));
            await ExpectListedAsync(page, Finished, Film);
        });
    }

    /// <summary>One film Weir finished and Deluno imported, and one whose pass fails every time and stays failed.</summary>
    private static async Task<ProcessingRig> StartRigWithAFailedAndAFinishedFileAsync()
    {
        var rig = await ProcessingRig.StartAsync(holdFailures: true);
        try
        {
            await rig.WithEvidenceAsync(async () =>
            {
                await rig.HandOffAsync("h-finished", Finished);
                rig.ReleasePass(Finished);
                await rig.WaitForWorkToEndAsync();
                await rig.WaitForReportAsync();
                await rig.ReportImportedAsync("h-finished");
                await rig.HandOffFailingAsync("h-failed", Film);
                await rig.WaitForWorkToEndAsync();
            });
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<IPage> OpenActivityAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Desktop);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        var rows = page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row);
        await Expect(rows.Filter(new() { HasText = Film })).ToContainTextAsync("Failed", new() { Timeout = PushMs });
        await Expect(rows.Filter(new() { HasText = Finished })).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });
        return page;
    }

    private static ILocator FileRows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = ListedFile() });

    /// <summary>The Files table lists exactly these files, in this order.</summary>
    private static async Task ExpectListedAsync(IPage page, params string[] names)
    {
        foreach (var file in new[] { Film, Finished })
        {
            await Expect(FileRows(page).Filter(new() { HasText = file })).ToHaveCountAsync(names.Contains(file) ? 1 : 0, new() { Timeout = PushMs });
        }

        Assert.Equal(names, await ListedNamesAsync(page));
    }

    /// <summary>The names of the files in the order the Files table lists them.</summary>
    private static async Task<IReadOnlyList<string>> ListedNamesAsync(IPage page)
    {
        var rows = await FileRows(page).AllInnerTextsAsync();
        return [.. rows.Select(text => ListedFile().Match(text).Value)];
    }

    /// <summary>The names of the two files in the order the server lists them when sorted by file.</summary>
    private static async Task<IReadOnlyList<string>> ServerOrderAsync(ProcessingRig rig, string direction)
    {
        var listed = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/files", ("sort", "file"), ("direction", direction));
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return [.. listed.Fields["files"]!.AsArray().Select(file => Path.GetFileName((string)file!["relative_path"]!))];
    }
}
