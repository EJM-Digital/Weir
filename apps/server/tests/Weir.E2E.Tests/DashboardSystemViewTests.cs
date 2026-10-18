using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Dashboard › System against a real Weir: the cards that run Weir's checks and tests (Health, Connections), the drawer that shows a
/// workflow's folder chain, the Scheduled tasks table's columns and the Back up now button.
/// </summary>
public sealed partial class DashboardSystemViewTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private static readonly ViewportSize Roomy = new() { Width = 1600, Height = 1000 };

    // Every value an attribute or text takes on the matching elements, in order, so a state that lasts a moment is still seen.
    private const string RecordStates = """
        window.__states = {};
        window.__record = (key, find, read) => {
          const watching = new MutationObserver(() => {
            const element = find();
            if (!element) return;
            const note = () => { const list = (window.__states[key] ??= []); const value = read(element); if (list[list.length - 1] !== value) list.push(value); };
            note();
            new MutationObserver(note).observe(element, { attributes: true, childList: true, characterData: true, subtree: true });
            watching.disconnect();
          });
          watching.observe(document, { childList: true, subtree: true });
        };
        window.__record('connection', () => document.querySelector('[data-testid="system-connection"]'), (e) => e.getAttribute('data-status'));
        window.__record('testAll', () => [...document.querySelectorAll('button')].find((b) => /^Test all|^Testing/.test(b.textContent)), (e) => e.textContent);
        window.__record('backup', () => [...document.querySelectorAll('button')].find((b) => /^Back up now|^Backing up/.test(b.textContent)), (e) => e.textContent);
        window.__record('health', () => [...document.querySelectorAll('button')].find((b) => /^Check now|^Checking/.test(b.textContent)), (e) => e.textContent);
        """;

    [GeneratedRegex(@"/api/v1/media-managers/connections/(\d+)/test$")]
    private static partial Regex ManagerTest();

    private async Task<IPage> OpenSystemAsync(ProcessingRig rig, ViewportSize? size = null)
    {
        var page = await NewPageAsync(size ?? Roomy);
        await page.AddInitScriptAsync(RecordStates);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/?view=system");
        await Expect(page.GetByTestId("dashboard-system")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("system-weir")).ToContainTextAsync("Version", new() { Timeout = PushMs });
        return page;
    }

    private static Task<string[]> StatesAsync(IPage page, string key) =>
        page.EvaluateAsync<string[]>("(key) => window.__states[key] ?? []", key);

    private static ILocator Card(IPage page, string name) => page.GetByRole(AriaRole.Region, new() { Name = name, Exact = true });

    [E2EFact]
    public async Task This_Weir_shows_how_Weir_is_running_as_the_server_reports_it()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);

        var facts = page.GetByTestId("system-weir").GetByRole(AriaRole.Group, new() { Name = "Facts about Weir" });
        await Expect(facts).ToContainTextAsync("Version");
        await Expect(facts).ToContainTextAsync("Uptime");
        var overview = await rig.Admin.GetAsync($"{WeirClient.Api}/system/overview");
        Assert.True(overview.Status == HttpStatusCode.OK, overview.ToString());
        await Expect(facts).ToContainTextAsync((string)overview.Fields["version"]!);
    }

    [E2EFact]
    public async Task A_workflow_checks_Details_opens_its_folder_chain_and_Close_Escape_and_Open_this_workflow_work()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var movies = await rig.MoviesWorkflowIdAsync();
        var health = Card(page, "Health");

        // Only a workflow's check has Details; a backup check or a tool does not.
        await Expect(health.GetByRole(AriaRole.Button, new() { Name = "Details: Movies", Exact = true })).ToBeVisibleAsync(new() { Timeout = PushMs });
        await Expect(health.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Details: (Configuration backup|FFmpeg)") })).ToHaveCountAsync(0);

        await health.GetByRole(AriaRole.Button, new() { Name = "Details: Movies", Exact = true }).ClickAsync();

        var drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "Movies", Exact = true });
        await Expect(drawer).ToBeVisibleAsync();
        var chain = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries/{movies}/folder-chain");
        Assert.True(chain.Status == HttpStatusCode.OK, chain.ToString());
        var watched = (string)(await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries/{movies}")).Fields["watched_folder"]!;
        await Expect(drawer).ToContainTextAsync($"Weir can read the watched folder {watched}.");

        await drawer.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Expect(drawer).ToHaveCountAsync(0);
        await health.GetByRole(AriaRole.Button, new() { Name = "Details: Movies", Exact = true }).ClickAsync();
        await Expect(drawer).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(drawer).ToHaveCountAsync(0);

        await health.GetByRole(AriaRole.Button, new() { Name = "Details: Movies", Exact = true }).ClickAsync();
        await drawer.GetByRole(AriaRole.Link, new() { Name = "Open this workflow", Exact = true }).ClickAsync();

        var editor = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit workflow" });
        await Expect(editor.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true })).ToHaveValueAsync("Movies");
        await Expect(page).ToHaveURLAsync(new Regex("/setup/workflows$"));
    }

    [E2EFact]
    public async Task The_Health_cards_Check_now_reads_every_folder_chain_again_and_tests_every_connection()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var health = Card(page, "Health");
        await Expect(health.GetByRole(AriaRole.Button, new() { Name = "Details: Movies", Exact = true })).ToBeVisibleAsync(new() { Timeout = PushMs });
        var sent = new List<string>();
        page.Request += (_, request) =>
        {
            lock (sent)
            {
                sent.Add($"{request.Method} {new Uri(request.Url).AbsolutePath}");
            }
        };

        await health.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }).ClickAsync();

        await Expect(health.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true })).ToBeEnabledAsync(new() { Timeout = PushMs });
        Assert.Equal(["Check now", "Checking…", "Check now"], (await StatesAsync(page, "health")).Take(3));
        lock (sent)
        {
            Assert.Equal(2, sent.Count(line => line.StartsWith("GET ", StringComparison.Ordinal) && line.EndsWith("/folder-chain", StringComparison.Ordinal)));
            Assert.Single(sent, line => line.StartsWith("POST ", StringComparison.Ordinal) && ManagerTest().IsMatch(line));
        }
    }

    [E2EFact]
    public async Task A_connections_Test_turns_its_row_blue_then_green_or_red_and_Test_all_tests_every_connection_that_can_be_tested()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var connections = Card(page, "Connections");
        var row = connections.GetByTestId("system-connection");
        var testAll = connections.GetByRole(AriaRole.Button, new() { Name = "Test all connections" });
        await Expect(row).ToHaveAttributeAsync("data-status", new Regex("."), new() { Timeout = PushMs });

        await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Test ") }).ClickAsync();

        await Expect(row).ToHaveAttributeAsync("data-status", "done", new() { Timeout = PushMs });
        var seen = await StatesAsync(page, "connection");
        Assert.Contains("doing", seen);
        Assert.Equal("done", seen[^1]);

        rig.StopDeluno();
        await testAll.ClickAsync();

        await Expect(row).ToHaveAttributeAsync("data-status", "broken", new() { Timeout = PushMs });
        await Expect(testAll).ToBeEnabledAsync();
        var buttonStates = await StatesAsync(page, "testAll");
        Assert.Contains("Testing…", buttonStates);
        Assert.Equal("Test all", buttonStates[^1]);
    }

    [E2EFact]
    public async Task Test_all_is_unavailable_when_no_connection_can_be_tested()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var connection = (long)Assert.Single((await rig.Admin.GetAsync($"{WeirClient.Api}/media-managers/connections")).Elements)!["id"]!;
        var testAll = Card(page, "Connections").GetByRole(AriaRole.Button, new() { Name = "Test all connections" });
        await Expect(testAll).ToBeEnabledAsync(new() { Timeout = PushMs });

        var off = await rig.Admin.PutWithCsrfAsync($"{WeirClient.Api}/media-managers/connections/{connection}", new JsonObject { ["enabled"] = false });
        Assert.True(off.Status == HttpStatusCode.OK, off.ToString());

        await Expect(testAll).ToBeDisabledAsync(new() { Timeout = PushMs });
    }

    [E2EFact]
    public async Task The_Scheduled_tasks_columns_move_with_Alt_and_an_arrow_and_Reset_columns_puts_them_back()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var tasks = Card(page, "Scheduled tasks");
        var headings = tasks.GetByRole(AriaRole.Columnheader);
        await Expect(headings).ToHaveTextAsync(["Task", "Last result", "Next"], new() { Timeout = PushMs });

        await headings.First.GetByRole(AriaRole.Button).FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowRight");

        await Expect(headings).ToHaveTextAsync(["Last result", "Task", "Next"]);

        await tasks.GetByRole(AriaRole.Button, new() { Name = "Columns", Exact = true }).ClickAsync();
        await tasks.GetByRole(AriaRole.Menuitem, new() { Name = "Reset columns", Exact = true }).ClickAsync();

        await Expect(headings).ToHaveTextAsync(["Task", "Last result", "Next"]);
    }

    [E2EFact]
    public async Task Back_up_now_makes_a_backup_the_server_lists_and_the_button_says_Backing_up_while_it_does()
    {
        await using var rig = await ProcessingRig.StartAsync();
        var page = await OpenSystemAsync(rig);
        var backups = $"{WeirClient.Api}/suite/configuration-backups";
        var before = (await rig.Admin.GetAsync(backups)).Fields["items"]!.AsArray().Count;
        var button = Card(page, "Backups and tools").GetByRole(AriaRole.Button, new() { Name = "Back up now", Exact = true });

        await button.ClickAsync();

        await Poll.UntilAsync(
            async () => (await rig.Admin.GetAsync(backups)).Fields["items"]!.AsArray().Count == before + 1,
            "the backup to be listed by the server",
            TimeSpan.FromSeconds(30));
        await Expect(button).ToBeEnabledAsync(new() { Timeout = PushMs });
        await Expect(Card(page, "Backups and tools").GetByRole(AriaRole.Listitem).Filter(new() { HasTextRegex = new Regex(@"\d+(\.\d+)? (KB|MB)") })).ToHaveCountAsync(before + 1, new() { Timeout = PushMs });
        var states = await StatesAsync(page, "backup");
        Assert.Equal("Back up now", states[0]);
        Assert.Contains("Backing up…", states);
        Assert.Equal("Back up now", states[^1]);
    }
}
