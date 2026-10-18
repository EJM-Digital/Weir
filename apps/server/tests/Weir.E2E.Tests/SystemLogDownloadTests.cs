using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs › Export hands over the rows the filters show as a spreadsheet or as JSON, and the whole server log as the file it is. Each
/// is a real download, asked for with the filters the list is showing and checked against what the server itself gives for the same question.
/// </summary>
public sealed class SystemLogDownloadTests(E2EServer server) : E2ETestBase(server)
{
    private const string Marker = "export-marker";
    private const int Marked = 3;
    private const string Export = "/api/v1/system/log/export";
    private const string ServerLog = "/api/v1/suite/logs/download";
    private const float ServerStoppedMs = 20_000;

    private static ILocator ExportButton(IPage page) => page.GetByTestId("logs-export");

    private static ILocator Menu(IPage page) => page.GetByRole(AriaRole.Menu, new() { Name = "Export", Exact = true });

    private static ILocator Choice(IPage page, string label) => Menu(page).GetByRole(AriaRole.Menuitem, new() { NameRegex = new Regex($"^{Regex.Escape(label)}") });

    private async Task<SystemSession> OpenMarkedLogAsync()
    {
        var system = await SystemSession.OpenAsync(await NewPageAsync(), "About");
        for (var number = 1; number <= Marked; number++)
        {
            E2EDatabase.InsertActivityEvent(system.Server.DatabasePath, "auth.password_changed", "auth", $"{Marker} {number}", $"{Marker} {number} detail");
        }

        await system.Page.GotoAsync(
            $"{system.Server.BaseUrl.GetLeftPart(UriPartial.Authority)}/system?tab=logs&source=event&level=info,success&q={Marker}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(system.Page.GetByTestId("log-summary")).ToHaveTextAsync($"{Marked} entries matching · live");
        return system;
    }

    private static async Task<(IDownload File, IResponse Answer)> DownloadAsync(IPage page, string choice)
    {
        await ExportButton(page).ClickAsync();
        var answer = page.WaitForResponseAsync(response => response.Url.Contains("/system/log/export", StringComparison.Ordinal));
        var file = await page.RunAndWaitForDownloadAsync(() => Choice(page, choice).ClickAsync());
        return (file, await answer);
    }

    private static async Task<string> ReadAsync(IDownload file)
    {
        var path = await file.PathAsync();
        return await File.ReadAllTextAsync(path!);
    }

    [E2EFact]
    public async Task The_spreadsheet_and_the_JSON_are_the_rows_the_filters_show_asked_for_under_the_filters_names()
    {
        await using var system = await OpenMarkedLogAsync();
        var (page, reads) = (system.Page, ReadsSent.Watch(system.Page));
        await ExportButton(page).ClickAsync();
        await Expect(ExportButton(page)).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Menu(page).GetByRole(AriaRole.Menuitem)).ToHaveTextAsync(
        [
            new Regex("^Spreadsheet \\(CSV\\)"),
            new Regex("^JSON"),
            new Regex("^Whole server log"),
        ]);
        await ExportButton(page).ClickAsync();
        await PageTrail.WatchTextAsync(page, "Exporting…");

        var mark = reads.Mark();
        var (csv, csvAnswer) = await DownloadAsync(page, "Spreadsheet (CSV)");

        var csvAsked = Assert.Single(reads.Since(mark, Export));
        Assert.Equal(
            new Dictionary<string, string> { ["source"] = "event", ["level"] = "info,success", ["q"] = Marker, ["format"] = "csv" }.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            csvAsked.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal("text/csv", csvAsked.Accept);
        Assert.Contains(csv.SuggestedFilename, await csvAnswer.HeaderValueAsync("content-disposition"), StringComparison.Ordinal);
        Assert.EndsWith(".csv", csv.SuggestedFilename, StringComparison.Ordinal);
        var csvText = await ReadAsync(csv);
        Assert.Equal((await system.Admin.GetAsync($"{Export}?source=event&level=info,success&q={Marker}&format=csv")).Text, csvText);
        for (var number = 1; number <= Marked; number++)
        {
            Assert.Contains($"{Marker} {number}", csvText, StringComparison.Ordinal);
        }

        mark = reads.Mark();
        var (json, jsonAnswer) = await DownloadAsync(page, "JSON");

        var jsonAsked = Assert.Single(reads.Since(mark, Export));
        Assert.Equal("json", jsonAsked.Parameters["format"]);
        Assert.Equal("application/json", jsonAsked.Accept);
        Assert.Contains(json.SuggestedFilename, await jsonAnswer.HeaderValueAsync("content-disposition"), StringComparison.Ordinal);
        Assert.EndsWith(".json", json.SuggestedFilename, StringComparison.Ordinal);
        var rows = JsonNode.Parse(await ReadAsync(json))!.AsArray();
        Assert.Equal(Marked, rows.Count);
        Assert.All(rows, row => Assert.Contains(Marker, (string)row!["title"]!, StringComparison.Ordinal));
        Assert.True(await PageTrail.SawAsync(page, "Exporting…"), "The button never said it was exporting.");
        await Expect(ExportButton(page)).ToHaveTextAsync("Export");
        await Expect(ExportButton(page)).ToBeEnabledAsync();
    }

    [E2EFact]
    public async Task The_whole_server_log_downloads_as_the_lines_the_server_wrote()
    {
        await using var system = await OpenMarkedLogAsync();
        var (page, reads) = (system.Page, ReadsSent.Watch(system.Page));
        var mark = reads.Mark();
        await ExportButton(page).ClickAsync();

        var file = await page.RunAndWaitForDownloadAsync(() => Choice(page, "Whole server log").ClickAsync());

        var asked = Assert.Single(reads.Since(mark, ServerLog));
        Assert.Empty(asked.Parameters);
        Assert.Matches(@"^weir-log-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-\d{3}Z\.log$", file.SuggestedFilename);
        var lines = (await ReadAsync(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.False(string.IsNullOrEmpty((string?)JsonNode.Parse(line)!["message"])));
    }

    [E2EFact]
    public async Task The_menu_opens_from_the_keyboard_and_closes_with_Escape_a_click_outside_or_its_button()
    {
        await using var system = await OpenMarkedLogAsync();
        var page = system.Page;

        await ExportButton(page).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(Menu(page)).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(Menu(page)).ToHaveCountAsync(0);
        await Expect(ExportButton(page)).ToHaveAttributeAsync("aria-expanded", "false");

        await ExportButton(page).ClickAsync();
        await Expect(Menu(page)).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Log", Level = 2, Exact = true }).ClickAsync();
        await Expect(Menu(page)).ToHaveCountAsync(0);

        await ExportButton(page).ClickAsync();
        await Expect(Menu(page)).ToBeVisibleAsync();
        await ExportButton(page).ClickAsync();
        await Expect(Menu(page)).ToHaveCountAsync(0);

        // From the keyboard alone: the button, then the menu's choices in order, and Enter takes the one with the focus.
        await ExportButton(page).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        await Expect(Choice(page, "JSON")).ToBeFocusedAsync();
        var file = await page.RunAndWaitForDownloadAsync(() => page.Keyboard.PressAsync("Enter"));
        Assert.EndsWith(".json", file.SuggestedFilename, StringComparison.Ordinal);
        await Expect(Menu(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task An_export_that_cannot_be_made_says_so_in_plain_words_and_the_next_try_starts_clean()
    {
        await using var system = await OpenMarkedLogAsync();
        var page = system.Page;
        var downloads = new List<string>();
        page.Download += (_, file) => downloads.Add(file.SuggestedFilename);
        await system.Server.StopAsync();

        foreach (var choice in new[] { "Spreadsheet (CSV)", "JSON", "Whole server log" })
        {
            await ExportButton(page).ClickAsync();
            await Choice(page, choice).ClickAsync();

            await Expect(page.GetByTestId("logs-problems").GetByRole(AriaRole.Alert)).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = ServerStoppedMs });
            await Expect(ExportButton(page)).ToHaveTextAsync("Export");
            await Expect(ExportButton(page)).ToBeEnabledAsync();
        }

        Assert.Empty(downloads);
        await system.Server.RestartAsync(samePort: true);
        await Expect(page.GetByTestId("live-connection-banner")).ToHaveCountAsync(0, new() { Timeout = ServerStoppedMs });
        await ExportButton(page).ClickAsync();
        var file = await page.RunAndWaitForDownloadAsync(() => Choice(page, "JSON").ClickAsync());
        Assert.EndsWith(".json", file.SuggestedFilename, StringComparison.Ordinal);
        await Expect(page.GetByTestId("logs-problems")).ToHaveCountAsync(0);
    }
}
