using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs asks the server for the list the filters describe, and nothing else: every filter a link or a control sets reaches the server under its
/// own name, in the form the server reads, and an older page of the list is asked for with the cursor the page before gave.
/// </summary>
public sealed class SystemLogRequestTests(E2EServer server) : E2ETestBase(server)
{
    private const string Log = "/api/v1/system/log";

    /// <summary>Opens the address and waits until the list has been asked for, and answered, with exactly <paramref name="expected"/> (the page size aside).</summary>
    private static async Task AssertAskedForAsync(IPage page, ReadsSent reads, int mark, IReadOnlyDictionary<string, string> expected)
    {
        await Poll.UntilAsync(
            () =>
            {
                var asked = reads.Since(mark, Log);
                Assert.NotEmpty(asked);
                var parameters = asked[^1].Parameters.Where(pair => pair.Key != "limit").OrderBy(pair => pair.Key, StringComparer.Ordinal);
                Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), parameters);
                return Task.FromResult<string?>("asked");
            },
            $"the list to be asked for with {string.Join('&', expected.Select(pair => $"{pair.Key}={pair.Value}"))}");
        await Expect(page.GetByTestId("log-summary")).ToHaveAttributeAsync("aria-busy", "false");
    }

    [E2EFact]
    public async Task Each_filter_in_the_address_reaches_the_server_under_its_own_name()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var reads = ReadsSent.Watch(page);
        (string Address, Dictionary<string, string> Asked)[] views =
        [
            (
                "level=error,warning,success&category=processing,scans&workflow=1&q=ffmpeg&when=custom&from=2026-10-01T10:00&to=2026-10-02T11:30",
                new()
                {
                    ["level"] = "error,warning,success",
                    ["category"] = "processing,scans",
                    ["workflow"] = "1",
                    ["q"] = "ffmpeg",
                    ["from"] = "2026-10-01T10:00:00.000Z",
                    ["to"] = "2026-10-02T11:30:00.000Z",
                }),
            (
                "source=event&event_type=processing.handoff.accepted&result=failed&trigger=manual",
                new()
                {
                    ["source"] = "event",
                    ["event_type"] = "processing.handoff.accepted",
                    ["result"] = "failed",
                    ["trigger"] = "manual",
                }),
            ("source=job&status=pending,failed&job=7", new() { ["source"] = "job", ["status"] = "pending,failed", ["job"] = "7" }),
            ("source=server&stack=1", new() { ["source"] = "server", ["has_exception"] = "true" }),
            (string.Empty, new()),
        ];

        foreach (var (address, asked) in views)
        {
            var mark = reads.Mark();
            await page.GotoAsync($"{BaseUrl}/system?tab=logs&{address}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            await AssertAskedForAsync(page, reads, mark, asked);
        }
    }

    [E2EFact]
    public async Task The_controls_set_the_same_filters_the_address_does_and_Clear_filters_takes_them_all_off()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var reads = ReadsSent.Watch(page);
        await Navigation.OpenLogsAsync(page);

        var mark = reads.Mark();
        await Navigation.OpenLogsAsync(page, "Jobs");
        await AssertAskedForAsync(page, reads, mark, new Dictionary<string, string> { ["source"] = "job" });

        mark = reads.Mark();
        await page.GetByPlaceholder("Search the log").FillAsync("remux");
        await AssertAskedForAsync(page, reads, mark, new Dictionary<string, string> { ["source"] = "job", ["q"] = "remux" });
        await Expect(page).ToHaveURLAsync(new Regex(@"source=job&q=remux$"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Clear filters", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/system\?tab=logs$"));
        await Expect(page.GetByPlaceholder("Search the log")).ToHaveValueAsync(string.Empty);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Clear filters", Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Source" }).GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^All") })).ToHaveAttributeAsync("aria-pressed", "true");
    }
}
