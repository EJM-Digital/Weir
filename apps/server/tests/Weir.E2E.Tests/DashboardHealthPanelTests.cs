using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Health panel's Check now button tests every switched-on media manager and download client and reads every workflow's folders
/// again, says "Checking…" while it does and what it found after, and narrows to one workflow's own connections.
/// </summary>
public sealed partial class DashboardHealthPanelTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private static readonly ViewportSize Roomy = new() { Width = 1600, Height = 1000 };

    // The button's text at every change, so a test sees "Checking…" however short the check was.
    private const string RecordCheckButton = """
        (() => {
          window.__checkNow = [];
          const find = () => [...document.querySelectorAll('button')].find((b) => b.textContent === 'Check now' || b.textContent === 'Checking…');
          const watching = new MutationObserver(() => {
            const button = find();
            if (!button) return;
            const note = () => { const last = window.__checkNow[window.__checkNow.length - 1]; if (last !== button.textContent) window.__checkNow.push(button.textContent); };
            note();
            new MutationObserver(note).observe(button, { childList: true, characterData: true, subtree: true });
            watching.disconnect();
          });
          watching.observe(document, { childList: true, subtree: true });
        })();
        """;

    // The verdict chip of the TV row at every change.
    private const string RecordVerdicts = """
        (() => {
          window.__verdicts = [];
          new MutationObserver(() => {
            const row = [...document.querySelectorAll('[data-testid="live-health"] li')].find((li) => li.textContent.includes('TV'));
            const chip = row && [...row.children].map((c) => c.textContent).find((text) => text && !text.startsWith('TV') && text.length < 30);
            if (chip && window.__verdicts[window.__verdicts.length - 1] !== chip) window.__verdicts.push(chip);
          }).observe(document, { childList: true, subtree: true, characterData: true });
        })();
        """;

    [GeneratedRegex(@"/api/v1/media-managers/connections/(\d+)/test$")]
    private static partial Regex ManagerTest();

    [GeneratedRegex(@"/api/v1/download-clients/connections/(\d+)/test$")]
    private static partial Regex ClientTest();

    private static ILocator Panel(IPage page) => page.GetByRole(AriaRole.Region, new() { Name = "Health", Exact = true });

    private static async Task<long> AddDownloadClientAsync(ProcessingRig rig, FakeDownloadClient client)
    {
        var created = await rig.Admin.PostWithCsrfAsync($"{WeirClient.Api}/download-clients/connections", new JsonObject
        {
            ["kind"] = "qbittorrent",
            ["base_url"] = client.BaseUrl,
            ["username"] = "alice",
            ["password"] = "right",
            ["enabled"] = true,
        });
        Assert.True(created.Status is HttpStatusCode.Created or HttpStatusCode.OK, created.ToString());
        return (long)created.Fields["id"]!;
    }

    [E2EFact]
    public async Task Check_now_tests_every_connection_and_reads_the_folders_again_and_says_which_did_not_answer()
    {
        using var downloads = FakeDownloadClient.Start("qbittorrent", "alice", "right", "/downloads");
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var clientId = await AddDownloadClientAsync(rig, downloads);
            var page = await NewPageAsync(Roomy);
            await page.AddInitScriptAsync(RecordCheckButton);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            var connections = Panel(page).GetByRole(AriaRole.Region, new() { Name = "Connections", Exact = true }).GetByRole(AriaRole.Listitem);
            await Expect(connections).ToHaveCountAsync(2, new() { Timeout = PushMs });

            // The folders are read again for the workflows the panel already lists, so wait until it lists them with their verdicts.
            var workflows = Panel(page).GetByRole(AriaRole.Region, new() { Name = "Workflows", Exact = true }).GetByRole(AriaRole.Listitem);
            await Expect(workflows).ToHaveCountAsync(2, new() { Timeout = PushMs });
            await Expect(workflows.Filter(new() { HasText = "Movies" })).ToContainTextAsync("In sync", new() { Timeout = PushMs });
            await Expect(workflows.Filter(new() { HasText = "TV" })).ToContainTextAsync("Needs a fix", new() { Timeout = PushMs });

            // Only what the click asks for is counted: the panel has read everything it lists, and what it was told about that is settled.
            var sent = new List<string>();
            page.Request += (_, request) =>
            {
                lock (sent)
                {
                    sent.Add($"{request.Method} {new Uri(request.Url).AbsolutePath}");
                }
            };
            var button = Panel(page).GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true });

            await button.ClickAsync();

            await Expect(Panel(page).GetByRole(AriaRole.Status)).ToHaveTextAsync("Checked · all answered.", new() { Timeout = PushMs });
            await Expect(button).ToBeEnabledAsync();
            lock (sent)
            {
                Assert.Single(sent, line => line.StartsWith("POST ", StringComparison.Ordinal) && ManagerTest().IsMatch(line));
                var test = Assert.Single(sent, line => line.StartsWith("POST ", StringComparison.Ordinal) && ClientTest().IsMatch(line));
                Assert.Contains($"/connections/{clientId}/test", test, StringComparison.Ordinal);
                Assert.Equal(2, sent.Count(line => line.StartsWith("GET ", StringComparison.Ordinal) && line.EndsWith("/folder-chain", StringComparison.Ordinal)));
            }

            var texts = await page.EvaluateAsync<string[]>("() => window.__checkNow");
            Assert.Equal(["Check now", "Checking…", "Check now"], texts.Take(3));

            // Both connections stop answering: the next check says how many.
            downloads.Dispose();
            rig.StopDeluno();
            await button.ClickAsync();

            await Expect(Panel(page).GetByRole(AriaRole.Status)).ToHaveTextAsync("Checked · 2 didn't answer.", new() { Timeout = PushMs });
        });
    }

    [E2EFact]
    public async Task A_workflow_row_says_Checking_until_its_folder_chain_has_answered_and_then_the_verdict()
    {
        var page = await NewPageAsync(Roomy);
        await page.AddInitScriptAsync(RecordVerdicts);
        await Navigation.EnsureSignedInAsync(page, BaseUrl);

        var verdicts = Panel(page).GetByRole(AriaRole.Region, new() { Name = "Workflows", Exact = true }).GetByRole(AriaRole.Listitem);
        await Expect(verdicts.Filter(new() { HasText = "TV" })).ToContainTextAsync("Needs a fix", new() { Timeout = PushMs });
        var seen = await page.EvaluateAsync<string[]>("() => window.__verdicts");
        Assert.Contains("Checking…", seen);
        Assert.Equal("Needs a fix", seen[^1]);
    }
}
